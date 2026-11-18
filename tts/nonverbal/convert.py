"""Живые смех, плач, вздохи и стоны голосом Клавы (одноразовая сборка банка; нейросети — Python).

Друг 28.09: «смех и плач звучат как ахегао, смех ненатуральный». Смешки VoiceDesign были придыхательными и чужими по тембру.
Здесь — настоящие человеческие звуки из ASVP-ESD (Zenodo 7132783, CC BY 4.0; неречевые клипы женских голосов) переводятся
в тембр Клавы Seed-VC (Plachtaa/seed-vc, без обучения, эталон klava_02) и меряются: близость тембра к её голосу (WavLM),
тон (не «лолька»), длительность. Лучшие по каждому звуку — страница web/voices/emotions/nonverbal/nonverbal.html, друг
отмечает живые; сервер голоса вклеивает их на [смех], [плач], [вздох], [стон].

    tts\\.venv\\Scripts\\python.exe tts\\nonverbal\\convert.py [--per 40] [--keep 10] [--steps 30]
"""
import argparse
import json
import os
import random
import shutil
import sys
import time
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[2]
TTS = ROOT / "tts"
SEED = ROOT / "tools" / "seed-vc"
ASVP = ROOT / "data" / "emotion" / "asvp" / "ASVP-ESD-Update" / "Audio"
OUT = TTS / "voices" / "nonverbal"
WEB = ROOT / "web" / "voices" / "emotions" / "nonverbal"
sys.path.insert(0, str(TTS))
sys.path.insert(0, str(SEED))
# недостающее для Seed-VC (munch, matplotlib, урезанный dac) — В КОНЦЕ пути: numpy, torch и прочее — только из окружения голоса
sys.path.append(str(SEED / "_pkgs"))

import speaker_id  # noqa: E402
from emotion_voice import REF, to16k  # noqa: E402
from server import median_f0  # noqa: E402

# звук → (код эмоции ASVP-ESD у неречевых клипов, подкоды-хвосты, которые точно этот звук; пусто — любой)
KINDS = {
    "laugh": ("03", {"13", "23"}, "смех"),
    "cry": ("04", {"14", "34", "44"}, "плач, всхлипы"),
    "sigh": ("01", {"24", "31", "11", "01"}, "вздох"),
    "moan": ("10", set(), "стон удовольствия, томный выдох"),
    "groan": ("11", set(), "стон боли, кряхтение"),
    # для [ахегао] — комичные живые вскрики (друг 28.09: «ah, yamerooo!», а не озвучка текста)
    "squeal": ("09", set(), "восторженный визг, взвизг"),
    "gasp": ("08", set(), "ах! — вскрик удивления"),
}


def clip_info(p: Path) -> dict | None:
    parts = p.stem.split("-")
    if len(parts) < 9 or parts[0] != "03" or parts[1] != "02":
        return None
    if parts[-1] in ("77", "66"):      # шумно / несколько голосов
        return None
    return {"emo": parts[2], "tail": parts[-1], "actor": p.parent.name}


def quality(w: np.ndarray, sr: int) -> tuple[float, float]:
    """(длительность звучащего, с; разброс громкости дБ: тихий фон против звука — шумные записи отсеиваются)."""
    fr = int(sr * 0.03)
    n = len(w) // fr
    if n < 5:
        return 0.0, 0.0
    rms = np.sqrt(np.mean(w[:n * fr].reshape(n, fr) ** 2, axis=1)) + 1e-6
    loud = rms > max(0.01, np.percentile(rms, 90) * 0.1)
    return float(loud.sum() * 0.03), float(20 * np.log10(np.percentile(rms, 95) / np.percentile(rms, 10)))


def pick(per: int, rnd: random.Random) -> dict[str, list[Path]]:
    by: dict[str, list[tuple[float, Path]]] = {k: [] for k in KINDS}
    files = sorted(ASVP.rglob("*.wav"))
    for p in files:
        info = clip_info(p)
        if info is None:
            continue
        for kind, (emo, tails, _) in KINDS.items():
            if info["emo"] != emo or (tails and info["tail"] not in tails):
                continue
            try:
                w, sr = sf.read(p, dtype="float32")
            except RuntimeError:
                continue
            if w.ndim > 1:
                w = w.mean(axis=1)
            dur = len(w) / sr
            if not 0.5 <= dur <= 5.0:
                continue
            sound, dyn = quality(w, sr)
            f0 = median_f0(w, sr)
            # женский голос (у смеха и плача тон скачет — медиана выше 180 Гц) и чистая запись
            if f0 < 180 or dyn < 18 or sound < 0.35:
                continue
            by[kind].append((dyn + rnd.random() * 4, p))
    out = {}
    for kind, lst in by.items():
        lst.sort(key=lambda x: -x[0])
        out[kind] = [p for _, p in lst[:per]]
        print(f"{kind}: подходящих {len(lst)}, беру {len(out[kind])}", flush=True)
    return out


class Converter:
    """
    Seed-VC, модели грузятся один раз; веса — из общего кэша HF. f0 — модель с тоном (DiT whisper-base f0, BigVGAN 44 кГц,
    RMVPE): рисунок тона исходника сохраняется и сдвигается к её среднему тону. Без тона (whisper-small, 22 кГц) модель
    придумывает тон сама — на смехе и плаче вышло «обезьянье урчание» (друг 28.09).
    """

    def __init__(self, steps: int, cfg_rate: float, f0: bool = True):
        from huggingface_hub import hf_hub_download
        os.chdir(SEED)
        import inference as vc
        # веса уже в общем кэше HF (tts/nonverbal/download.py) — не качать второй раз в ./checkpoints
        def from_cache(repo_id, model_filename="pytorch_model.bin", config_filename=None):
            m = hf_hub_download(repo_id, model_filename)
            return (m, hf_hub_download(repo_id, config_filename)) if config_filename else m
        vc.load_custom_model_from_hf = from_cache
        # новый huggingface_hub не передаёт proxies/resume_download, а BigVGAN их требует — подставить по умолчанию
        from modules.bigvgan import bigvgan
        orig = bigvgan.BigVGAN._from_pretrained.__func__

        def bigvgan_from_pretrained(cls, *, proxies=None, resume_download=False, **kw):
            return orig(cls, proxies=proxies, resume_download=resume_download, **kw)
        bigvgan.BigVGAN._from_pretrained = classmethod(bigvgan_from_pretrained)
        if f0:
            ck = hf_hub_download("Plachta/Seed-VC", "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth")
            cf = hf_hub_download("Plachta/Seed-VC", "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml")
        else:
            ck = hf_hub_download("Plachta/Seed-VC", "DiT_seed_v2_uvit_whisper_small_wavenet_bigvgan_pruned.pth")
            cf = hf_hub_download("Plachta/Seed-VC", "config_dit_mel_seed_uvit_whisper_small_wavenet.yml")
        args = SimpleNamespace(fp16=True, f0_condition=f0, checkpoint=ck, config=cf)
        self.vc = vc
        self.use_f0 = f0
        (self.model, self.semantic_fn, self.f0_fn, self.vocoder_fn, self.campplus, self.mel_fn, mel_args) = vc.load_models(args)
        self.sr = mel_args["sampling_rate"]
        self.steps, self.cfg_rate = steps, cfg_rate
        self.semitones = 0.0   # сдвиг тона относительно её среднего (from_ref.py: вариант «чуть звонче»)
        import librosa
        import torch
        import torchaudio
        self.torch, self.ta, self.librosa = torch, torchaudio, librosa
        ref = librosa.load(str(REF), sr=self.sr)[0][: self.sr * 25]
        self.ref = torch.tensor(ref).unsqueeze(0).float().to(vc.device)
        ref16 = torchaudio.functional.resample(self.ref, self.sr, 16000)
        self.S_ori = self.semantic_fn(ref16)
        self.mel2 = self.mel_fn(self.ref)
        feat2 = torchaudio.compliance.kaldi.fbank(ref16, num_mel_bins=80, dither=0, sample_frequency=16000)
        self.style2 = self.campplus((feat2 - feat2.mean(dim=0, keepdim=True)).unsqueeze(0))
        self.f0_ref = None
        if f0:
            self.f0_ref = torch.from_numpy(self.f0_fn(ref16[0], thred=0.03)).to(vc.device)[None]
            voiced = self.f0_ref[self.f0_ref > 1]
            self.ref_log_median = torch.median(torch.log(voiced + 1e-5))

    def __call__(self, path: Path) -> np.ndarray:
        torch, vc = self.torch, self.vc
        src = self.librosa.load(str(path), sr=self.sr)[0]
        with torch.no_grad():
            s = torch.tensor(src).unsqueeze(0).float().to(vc.device)
            s16 = self.ta.functional.resample(s, self.sr, 16000)
            S_alt = self.semantic_fn(s16)
            mel = self.mel_fn(s)
            f0_alt = None
            if self.use_f0:
                # рисунок тона исходника — как есть, только средний уровень к её голосу (иначе смех выходил писком или басом)
                f0_src = torch.from_numpy(self.f0_fn(s16[0], thred=0.03)).to(vc.device)[None]
                voiced = f0_src > 1
                f0_alt = f0_src.clone()
                if voiced.any():
                    logf = torch.log(f0_src + 1e-5)
                    f0_alt[voiced] = torch.exp(logf[voiced] - torch.median(logf[voiced]) + self.ref_log_median
                                               + self.semitones * 0.05776)   # ln 2 / 12
            cond = self.model.length_regulator(S_alt, ylens=torch.LongTensor([mel.size(2)]).to(mel.device), n_quantizers=3, f0=f0_alt)[0]
            prompt = self.model.length_regulator(self.S_ori, ylens=torch.LongTensor([self.mel2.size(2)]).to(mel.device), n_quantizers=3, f0=self.f0_ref)[0]
            cat = torch.cat([prompt, cond], dim=1)
            with torch.autocast(device_type=vc.device.type, dtype=torch.float16):
                out = self.model.cfm.inference(cat, torch.LongTensor([cat.size(1)]).to(mel.device), self.mel2, self.style2, None,
                                               self.steps, inference_cfg_rate=self.cfg_rate)
            out = out[:, :, self.mel2.size(-1):]
            wave = self.vocoder_fn(out.float()).squeeze().cpu().numpy()
        return wave.astype(np.float32)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--per", type=int, default=40, help="сколько исходников на звук переводить")
    ap.add_argument("--keep", type=int, default=10, help="сколько лучших на звук показать")
    ap.add_argument("--steps", type=int, default=30)
    ap.add_argument("--cfg", type=float, default=0.7)
    ap.add_argument("--no-f0", action="store_true", help="модель без тона (22 кГц) — на смехе и плаче урчит, только для сравнения")
    ap.add_argument("--kinds", default="", help="только эти звуки через запятую (squeal,gasp); остальные банки не трогать")
    a = ap.parse_args()
    rnd = random.Random(7)
    chosen = pick(a.per, rnd)
    only = [k.strip() for k in a.kinds.split(",") if k.strip()]
    if only:
        chosen = {k: v for k, v in chosen.items() if k in only}
    speaker_id.load_encoder()
    rw, rs = sf.read(REF, dtype="float32")
    remb = speaker_id.embed_audio(to16k(rw, rs))
    conv = Converter(a.steps, a.cfg, f0=not a.no_f0)
    OUT.mkdir(parents=True, exist_ok=True)
    WEB.mkdir(parents=True, exist_ok=True)
    old_report = OUT / "nonverbal.json"
    report: dict[str, list[dict]] = json.loads(old_report.read_text(encoding="utf-8")) if only and old_report.exists() else {}
    for kind, paths in chosen.items():
        rows = []
        for i, p in enumerate(paths):
            t = time.time()
            try:
                w = conv(p)
            except (RuntimeError, ValueError) as ex:
                print(f"{kind} {p.name}: {type(ex).__name__}: {ex}", flush=True)
                continue
            peak = float(np.max(np.abs(w))) or 1.0
            w = w / peak * 0.9
            e = speaker_id.embed_audio(to16k(w, conv.sr))
            sim = float(np.dot(remb, e) / (np.linalg.norm(remb) * np.linalg.norm(e) + 1e-9))
            f0 = median_f0(w, conv.sr)
            name = f"{kind}_{i:02d}.wav"
            sf.write(OUT / name, w, conv.sr)
            rows.append({"file": name, "src": str(p.relative_to(ASVP)), "sim": round(sim, 3), "f0": round(f0),
                         "dur": round(len(w) / conv.sr, 2), "sec": round(time.time() - t, 1)})
            print(json.dumps({"kind": kind, **rows[-1]}, ensure_ascii=False), flush=True)
        # лучшие: тембр ближе к ней, тон не выше 480 Гц (смех и плач выше речи, но не писк)
        rows.sort(key=lambda r: -(r["sim"] - (0.15 if r["f0"] > 480 else 0)))
        report[kind] = rows
        for r in rows[: a.keep]:
            shutil.copyfile(OUT / r["file"], WEB / r["file"])
            src = WEB / ("src_" + r["file"])
            shutil.copyfile(ASVP / r["src"], src)
    (OUT / "nonverbal.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    html = ["<!doctype html><meta charset='utf-8'><title>Живые звуки Клавы</title>",
            "<style>body{font:14px Segoe UI,sans-serif;background:#16141f;color:#eee;margin:16px}td{padding:4px 8px;"
            "border-bottom:1px solid #333}h2{color:#7ff3ff}.dim{color:#888}</style>",
            "<h1>Живые смех, плач, вздохи и стоны — её голосом</h1>",
            "<p>Настоящие звуки людей (ASVP-ESD), переведённые в тембр Клавы (Seed-VC). Слева — как вышло у неё, справа — исходник. "
            "Отметь живые номерами (например «laugh_03, cry_01») — их она будет вклеивать на [смех], [плач], [вздох], [стон]. "
            "Тембр — близость к её голосу (0,8+ хорошо).</p>"]
    for kind, (_, _, ru) in KINDS.items():
        html.append(f"<h2>{ru}</h2><table>")
        for r in report.get(kind, [])[: a.keep]:
            html.append(f"<tr><td>{r['file'][:-4]}</td><td><audio controls src='{r['file']}'></audio></td>"
                        f"<td class='dim'>исходник <audio controls src='src_{r['file']}'></audio></td>"
                        f"<td>тембр {r['sim']:.2f}</td><td>{r['f0']} Гц</td><td>{r['dur']} с</td></tr>")
        html.append("</table>")
    (WEB / "nonverbal.html").write_text("\n".join(html), encoding="utf-8")
    print("ГОТОВО", WEB / "nonverbal.html")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
