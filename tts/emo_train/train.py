"""Эмоциональная LoRA голоса Клавы (микротренировка Qwen3-TTS-12Hz-1.7B, нейросеть).

Учит модель слушаться указания манеры (instruct) для любого голоса: вход — отпечаток голоса диктора (x-vector другой
его фразы), текст и манера; цель — настоящая эмоциональная речь RESD (+ шёпот из LPC). Потом голос Клавы
получает ту же манеру со своим отпечатком — тембр её, интонация настоящая.

Вход собирается ровно так, как его собирает генерация (qwen_tts generate, режим «только отпечаток», непотоковый,
язык русский): [манера] + «<|im_start|>assistant\\n» + (tts_pad×5+tts_bos ⊕ think,think_bos,ru,think_eos,отпечаток,pad)
+ (текст+tts_eos ⊕ codec_pad) + (tts_pad ⊕ codec_bos) + кадры (tts_pad ⊕ сумма 16 групп кода). Официальный sft_12hz.py
так не делает (нет text_projection, двойной сдвиг меток, хвост кода от соседнего кадра) — потери здесь считаются
вручную, без сдвига внутри HF. Перед обучением — проверка выравнивания на исходной модели: правильно собранный вход
даёт заметно меньшую ошибку, чем сдвинутый на кадр.

    tts\\.venv\\Scripts\\python.exe emo_train\\train.py [--epochs 3] [--rank 16] [--check-only]
Итог: models/emo_lora/adapter.safetensors + adapter.json (для слияния — emo_train\\merge.py).
"""
from __future__ import annotations

import argparse
import json
import math
import random
import sys
import time
from collections import defaultdict
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
import torch.nn.functional as F

TTS = Path(__file__).resolve().parent.parent
ROOT = TTS.parent
MODEL = ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress"
DATA = ROOT / "data" / "emotion"
OUT = ROOT / "models" / "emo_lora"
TARGETS = ("q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj")


def log(m: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {m}", flush=True)


class Builder:
    """Вход talker'а для одной фразы — как в generate (без манеры — как у спокойного голоса)."""

    def __init__(self, tts):
        self.tts = tts
        self.m = tts.model
        self.T = self.m.talker
        c, tc = self.m.config, self.m.config.talker_config
        self.c, self.tc = c, tc
        self.dev = self.T.device
        self.langs = tc.codec_language_id   # у каждой фразы свой язык: RESD — russian, NonverbalTTS — english
        self._spk: dict[str, torch.Tensor] = {}

    def ids(self, s: str) -> torch.Tensor:
        return self.tts._tokenize_texts([s])[0].to(self.dev)

    def spk(self, path: str) -> torch.Tensor:
        if path not in self._spk:
            wav, sr = sf.read(path, dtype="float32")
            with torch.inference_mode():
                e = self.m.extract_speaker_embedding(wav, sr)
            self._spk[path] = e.clone().detach()
        return self._spk[path]

    def build(self, text: str, instruct: str, ref: str, codes: torch.Tensor, lang: str = "russian"):
        """→ (вход [1,L,H], позиция «старта» (предсказывает кадр 0), число кадров)."""
        T, c, tc = self.T, self.c, self.tc
        tp, te, ce = T.text_projection, T.get_text_embeddings(), T.get_input_embeddings()
        cpe = T.code_predictor.get_input_embeddings()
        dev = self.dev
        L = lambda xs: torch.tensor([xs], device=dev, dtype=torch.long)  # noqa: E731
        bos_e, eos_e, pad_e = tp(te(L([c.tts_bos_token_id, c.tts_eos_token_id, c.tts_pad_token_id]))).chunk(3, dim=1)
        parts = []
        if instruct:
            parts.append(tp(te(self.ids(f"<|im_start|>user\n{instruct}<|im_end|>\n"))))
        tid = self.ids(f"<|im_start|>assistant\n{text}<|im_end|>\n<|im_start|>assistant\n")
        parts.append(tp(te(tid[:, :3])))
        c0 = ce(L([tc.codec_think_id, tc.codec_think_bos_id, self.langs[lang], tc.codec_think_eos_id]))
        c1 = ce(L([tc.codec_pad_id, tc.codec_bos_id]))
        codec_in = torch.cat([c0, self.spk(ref).to(c0.dtype).view(1, 1, -1), c1], dim=1)          # 7
        parts.append(torch.cat([pad_e.expand(-1, codec_in.shape[1] - 2, -1), bos_e], dim=1) + codec_in[:, :-1])
        body = tid[:, 3:-5]
        parts.append(torch.cat([tp(te(body)), eos_e], dim=1) + ce(L([tc.codec_pad_id] * (body.shape[1] + 1))))
        parts.append(pad_e + ce(L([tc.codec_bos_id])))
        start = sum(p.shape[1] for p in parts) - 1
        codes = codes.to(dev)                                                                      # [F,16]
        frames = ce(codes[:, 0].unsqueeze(0))
        for g in range(1, codes.shape[1]):
            frames = frames + cpe[g - 1](codes[:, g].unsqueeze(0))
        parts.append(frames + pad_e)
        return torch.cat(parts, dim=1), start, codes.shape[0]

    def losses(self, text: str, instruct: str, ref: str, codes: torch.Tensor, shift: int = 0, lang: str = "russian"):
        """(ошибка основного кода, ошибка хвоста кода). shift — отладка выравнивания (сдвиг меток на кадры)."""
        x, start, n = self.build(text, instruct, ref, codes, lang)
        mask = torch.ones(x.shape[:2], device=self.dev, dtype=torch.long)
        out = self.T(inputs_embeds=x, attention_mask=mask, output_hidden_states=True)
        hidden = out.hidden_states[0][-1][0]                                                       # [L,H]
        logits = out.logits[0]
        codes = codes.to(self.dev)
        eos = self.tc.codec_eos_token_id
        target0 = torch.cat([codes[:, 0], torch.tensor([eos], device=self.dev)])                   # n+1
        pos = torch.arange(start, start + n + 1, device=self.dev)
        if shift:
            target0 = torch.roll(target0, -shift)
        l0 = F.cross_entropy(logits[pos].float(), target0)
        # хвост кода кадра k: скрытое состояние, что предсказало его основной код (start+k), + коды 0..14 → 1..15
        th = hidden[start:start + n]
        sub_logits, _ = self.T.forward_sub_talker_finetune(codes, th)
        l1 = F.cross_entropy(sub_logits.reshape(-1, sub_logits.shape[-1]).float(), codes[:, 1:].reshape(-1))
        return l0, l1


def load_jsonl(p: Path) -> list[dict]:
    return [json.loads(l) for l in p.read_text(encoding="utf-8").splitlines() if l.strip()]


def inject_lora(talker, rank: int, alpha: int):
    from peft import LoraConfig, inject_adapter_in_model
    cfg = LoraConfig(r=rank, lora_alpha=alpha, lora_dropout=0.05, target_modules=list(TARGETS), bias="none")
    inject_adapter_in_model(cfg, talker)
    n = 0
    for name, p in talker.named_parameters():
        p.requires_grad = "lora_" in name
        n += p.numel() if p.requires_grad else 0
    return cfg, n


@torch.no_grad()
def evaluate(b: Builder, rows: list[dict], per_style: int = 20) -> dict:
    """Ошибка основного кода по стилям — не больше per_style фраз на стиль (редкий стиль не тонет в частых)."""
    per = defaultdict(list)
    for r in rows:
        if len(per[r["style"]]) >= per_style:
            continue
        l0, l1 = b.losses(r["text"], r["instruct"], r["ref"], torch.tensor(r["codes"]), lang=r.get("lang", "russian"))
        per[r["style"]].append(float(l0))
    return {k: round(float(np.mean(v)), 3) for k, v in per.items()}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--epochs", type=int, default=3)
    ap.add_argument("--rank", type=int, default=16)
    ap.add_argument("--lr", type=float, default=1e-4)
    ap.add_argument("--accum", type=int, default=8)
    ap.add_argument("--check-only", action="store_true")
    ap.add_argument("--extra", default="nv", help="доп. наборы data/emotion/train_<имя>.jsonl через запятую (nv — смех в речи); пусто — только RESD")
    ap.add_argument("--out", default=str(OUT), help="куда сохранить адаптер (новый — рядом, чтобы не затереть рабочий, пока не проверен)")
    a = ap.parse_args()
    out_dir = Path(a.out)
    from qwen_tts import Qwen3TTSModel
    t = time.time()
    tts = Qwen3TTSModel.from_pretrained(str(MODEL), device_map="cuda:0", dtype=torch.bfloat16, attn_implementation="sdpa")
    log(f"модель {MODEL.name} за {time.time() - t:.0f} с, VRAM {torch.cuda.memory_allocated() / 2**30:.2f} ГиБ")
    b = Builder(tts)
    train = load_jsonl(DATA / "train.jsonl")
    val = load_jsonl(DATA / "val.jsonl")
    for name in [x for x in a.extra.split(",") if x.strip()]:
        tr, va = DATA / f"train_{name.strip()}.jsonl", DATA / f"val_{name.strip()}.jsonl"
        if tr.exists():
            train += load_jsonl(tr)
            val += load_jsonl(va) if va.exists() else []
            log(f"набор {name}: + учёба {sum(1 for _ in open(tr, encoding='utf-8'))}")
    random.Random(1).shuffle(val)
    log(f"фраз: учёба {len(train)}, проверка {len(val)}")

    # проверка выравнивания: на исходной модели правильный вход даёт ошибку заметно ниже сдвинутого
    b.T.eval()
    with torch.no_grad():
        calm = [r for r in val if r["style"] == "calm"][:12]
        right = np.mean([float(b.losses(r["text"], "", r["ref"], torch.tensor(r["codes"]))[0]) for r in calm])
        off = np.mean([float(b.losses(r["text"], "", r["ref"], torch.tensor(r["codes"]), shift=1)[0]) for r in calm])
        sub = np.mean([float(b.losses(r["text"], "", r["ref"], torch.tensor(r["codes"]))[1]) for r in calm])
    log(f"выравнивание: основной код {right:.3f} (сдвинутый на кадр {off:.3f}), хвост кода {sub:.3f}")
    if not right < off - 0.3:
        log("ВЫРАВНИВАНИЕ НЕВЕРНОЕ — правильный вход не лучше сдвинутого, учить нельзя")
        return 1
    base = evaluate(b, val)
    log(f"до учёбы, ошибка по стилям (проверка): {base}")
    if a.check_only:
        return 0

    cfg, n = inject_lora(b.T, a.rank, a.rank * 2)
    log(f"LoRA r={a.rank}: обучаемых параметров {n / 1e6:.1f} млн")
    if n == 0:
        log("LoRA не встала — нет модулей " + ",".join(TARGETS))
        return 1
    params = [p for p in b.T.parameters() if p.requires_grad]
    opt = torch.optim.AdamW(params, lr=a.lr, weight_decay=0.0)
    steps = a.epochs * math.ceil(len(train) / a.accum)
    sched = torch.optim.lr_scheduler.LambdaLR(opt, lambda s: min(1.0, (s + 1) / 30) * 0.5 * (1 + math.cos(math.pi * min(1.0, s / steps))))
    out_dir.mkdir(parents=True, exist_ok=True)
    best = float("inf")
    step = 0
    for ep in range(a.epochs):
        b.T.train()
        random.Random(ep).shuffle(train)
        run = 0.0
        t0 = time.time()
        for i, r in enumerate(train):
            codes = torch.tensor(r["codes"])
            if codes.shape[0] > 160:          # длинные (>13 с) — мимо: памяти впритык
                continue
            with torch.autocast("cuda", dtype=torch.bfloat16):
                l0, l1 = b.losses(r["text"], r["instruct"], r["ref"], codes, lang=r.get("lang", "russian"))
                loss = (l0 + 0.3 * l1) / a.accum
            loss.backward()
            run += float(loss) * a.accum
            if (i + 1) % a.accum == 0:
                torch.nn.utils.clip_grad_norm_(params, 1.0)
                opt.step()
                sched.step()
                opt.zero_grad(set_to_none=True)
                step += 1
                if step % 20 == 0:
                    log(f"эпоха {ep + 1} шаг {step}/{steps}: ошибка {run / (a.accum * 20):.3f}, {(time.time() - t0) / (i + 1):.2f} с/фразу")
                    run = 0.0
        b.T.eval()
        res = evaluate(b, val)
        mean = float(np.mean(list(res.values())))
        log(f"эпоха {ep + 1}: ошибка на проверке {res} (среднее {mean:.3f})")
        if mean < best:
            best = mean
            from peft import get_peft_model_state_dict
            from safetensors.torch import save_file
            sd = {k: v.detach().to(torch.float32).cpu().contiguous() for k, v in get_peft_model_state_dict(b.T).items()}
            save_file(sd, str(out_dir / "adapter.safetensors"))
            (out_dir / "adapter.json").write_text(json.dumps({
                "base": MODEL.name, "rank": a.rank, "alpha": a.rank * 2, "targets": list(TARGETS), "epoch": ep + 1,
                "val": res, "before": base, "prefix": "talker.",
                "styles": sorted({r["style"]: r["instruct"] for r in train}.items()),
            }, ensure_ascii=False, indent=1), encoding="utf-8")
            log(f"сохранила адаптер (эпоха {ep + 1})")
    log("ГОТОВО")
    return 0


if __name__ == "__main__":
    sys.exit(main())
