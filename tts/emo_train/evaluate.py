"""Проверка эмоций голоса Клавы: исходная модель против дообученной (нейросеть, одноразовая проверка).

Голос Клавы (отпечаток klava_02, режим «только отпечаток», непотоковый — как учили) произносит нейтральные фразы
с манерой каждой эмоции: эмоцию должна дать манера, а не текст. Классификатор эмоций речи (Aniemore wav2vec2,
RESD) говорит, что слышит; WavLM — осталась ли она собой (тембр); плюс тон. Итог — таблица и страница
web/voices/emotions/lora.html «до / после».

    tts\\.venv\\Scripts\\python.exe emo_train\\evaluate.py [--models base,emo] [--n 5]
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
import torch

TTS = Path(__file__).resolve().parent.parent
ROOT = TTS.parent
sys.path.insert(0, str(TTS))
import speaker_id  # noqa: E402
from emotion_voice import REF, to16k  # noqa: E402
from server import median_f0  # noqa: E402

SER = ROOT / "models" / "ser-wav2vec2-ru"
MODELS = {"base": ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress",
          "emo": ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress-emo"}
WEB = ROOT / "web" / "voices" / "emotions" / "lora"
# стиль голоса → класс RESD, который должен услышать классификатор
TARGET = {"angry": "anger", "annoyed": "disgust", "happy": "happiness", "sad": "sadness", "scared": "fear", "calm": "neutral"}
TEXTS = [
    "Я не знаю, что ты сейчас скажешь, но я слушаю.",
    "Сегодня мы опять засиделись допоздна.",
    "Ты правда это сделал сам?",
    "Ну вот, опять всё начинается сначала.",
    "Давай посмотрим, что там получилось.",
    "Я только что вернулась домой и сразу к тебе.",
]


def load_ser():
    spec = importlib.util.spec_from_file_location("ser_pkg", SER / "__init__.py", submodule_search_locations=[str(SER)])
    pkg = importlib.util.module_from_spec(spec)
    sys.modules["ser_pkg"] = pkg
    spec.loader.exec_module(pkg)
    from ser_pkg.wav2vec2fsr_config import W2V2FSRConfig
    from ser_pkg.wav2vec2speechclassification import Wav2Vec2ForSpeechClassification
    from transformers import Wav2Vec2FeatureExtractor
    # класс написан под старый transformers: в конструкторе init_weights(), а пятому нужен post_init() — он и сам
    # зовёт init_weights, поэтому внутренний вызов отдаём штатной инициализации (иначе бесконечная рекурсия)
    from transformers import PreTrainedModel
    base_init = PreTrainedModel.init_weights

    def init_weights(self):
        if getattr(self, "_klava_in_post", False):
            return base_init(self)
        self._klava_in_post = True
        try:
            self.post_init()
        finally:
            self._klava_in_post = False

    Wav2Vec2ForSpeechClassification.init_weights = init_weights
    cfg = W2V2FSRConfig.from_pretrained(str(SER))
    model = Wav2Vec2ForSpeechClassification.from_pretrained(str(SER), config=cfg).eval().to("cuda")
    return model, Wav2Vec2FeatureExtractor.from_pretrained(str(SER)), cfg.id2label


@torch.no_grad()
def classify(ser, wav: np.ndarray, sr: int) -> dict[str, float]:
    model, fe, labels = ser
    x = librosa.resample(wav.astype(np.float32), orig_sr=sr, target_sr=16000)
    inp = fe(x, sampling_rate=16000, return_tensors="pt", padding=True)
    logits = model(inp.input_values.to("cuda")).logits[0].float()
    p = torch.softmax(logits, -1).cpu().numpy()
    return {labels[i] if isinstance(labels, list) else labels[str(i)] if str(i) in labels else labels[i]: float(p[i]) for i in range(len(p))}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default="base,emo")
    ap.add_argument("--n", type=int, default=5)
    a = ap.parse_args()
    from faster_qwen3_tts import FasterQwen3TTS
    import ru_text
    ru_text.load()
    adapter = ROOT / "models" / "emo_lora" / "adapter.json"
    if adapter.exists():
        styles = dict(json.loads(adapter.read_text(encoding="utf-8"))["styles"])
    else:
        # адаптера ещё нет — манеры из подготовки данных (замер «до»)
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from prepare import STYLES, WHISPER
        styles = {s: i for s, i in STYLES.values()}
        styles[WHISPER[0]] = WHISPER[1]
    styles["calm"] = ""
    speaker_id.load_encoder()
    rw, rs = sf.read(REF, dtype="float32")
    remb = speaker_id.embed_audio(to16k(rw, rs))
    ser = load_ser()
    WEB.mkdir(parents=True, exist_ok=True)
    report = {}
    for mname in a.models.split(","):
        mdir = MODELS[mname]
        if not (mdir / "model.safetensors").exists():
            print(f"{mname}: нет модели {mdir}")
            continue
        tts = FasterQwen3TTS.from_pretrained(str(mdir), device="cuda", dtype=torch.bfloat16, attn_implementation="sdpa", max_seq_len=1024)
        for style, instruct in styles.items():
            rows = []
            for i, text in enumerate(TEXTS[:a.n]):
                torch.manual_seed(100 + i)
                kw = {"instruct": instruct} if instruct else {}
                wavs, sr = tts.generate_voice_clone(text=ru_text.prepare(text)[0], language="Russian", ref_audio=str(REF),
                                                    ref_text="", xvec_only=True, non_streaming_mode=True,
                                                    temperature=0.55, top_k=20, top_p=0.9, **kw)
                wav = np.asarray(wavs[0], dtype=np.float32)
                name = f"{mname}_{style}_{i}.wav"
                sf.write(WEB / name, wav, sr)
                probs = classify(ser, wav, sr)
                e = speaker_id.embed_audio(to16k(wav, sr))
                rows.append({"file": name, "text": text, "probs": probs,
                             "sim": float(np.dot(remb, e) / (np.linalg.norm(remb) * np.linalg.norm(e) + 1e-9)),
                             "f0": median_f0(wav, sr)})
            tgt = TARGET.get(style)
            summary = {
                "p_target": round(float(np.mean([r["probs"].get(tgt, 0) for r in rows])), 3) if tgt else None,
                "hit": round(float(np.mean([max(r["probs"], key=r["probs"].get) == tgt for r in rows])), 2) if tgt else None,
                "sim": round(float(np.mean([r["sim"] for r in rows])), 3),
                "f0": round(float(np.median([r["f0"] for r in rows if r["f0"] > 0] or [0])), 0),
                "top": max(np.mean([[r["probs"][k] for k in rows[0]["probs"]] for r in rows], axis=0).tolist()),
                "rows": rows,
            }
            report.setdefault(mname, {})[style] = summary
            print(f"{mname:4s} {style:8s} цель {tgt or '—':10s} P {summary['p_target']} попадание {summary['hit']} "
                  f"тембр {summary['sim']:.2f} тон {summary['f0']:.0f}", flush=True)
        tts = None
        torch.cuda.empty_cache()
    (WEB / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    names = {"calm": "спокойствие", "angry": "злость", "annoyed": "раздражение", "happy": "радость", "sad": "грусть",
             "scared": "испуг", "whisper": "шёпот"}
    html = ["<!doctype html><meta charset='utf-8'><title>Эмоции голоса Клавы: до и после</title>",
            "<style>body{font:14px Segoe UI,sans-serif;background:#16141f;color:#eee;margin:16px}td{padding:4px 8px;"
            "border-bottom:1px solid #333;vertical-align:top}h2{color:#7ff3ff}</style>",
            "<h1>Эмоции голоса Клавы: до и после микротренировки</h1>"
            "<p>Фраза нейтральная — эмоцию даёт только манера. «Слышит» — что говорит классификатор эмоций речи.</p>"]
    for style in styles:
        html.append(f"<h2>{names.get(style, style)}</h2><table><tr><td>фраза</td>" +
                    "".join(f"<td>{m}</td>" for m in report) + "</tr>")
        for i, text in enumerate(TEXTS[:a.n]):
            cells = []
            for m in report:
                r = report[m].get(style, {}).get("rows", [])
                if i < len(r):
                    top = max(r[i]["probs"], key=r[i]["probs"].get)
                    cells.append(f"<td><audio controls src='{r[i]['file']}'></audio><br>слышит: {top} "
                                 f"{r[i]['probs'][top]:.0%}, тембр {r[i]['sim']:.2f}</td>")
            html.append(f"<tr><td>{text}</td>{''.join(cells)}</tr>")
        html.append("</table>")
    (WEB / "lora.html").write_text("\n".join(html), encoding="utf-8")
    print("страница:", WEB / "lora.html")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
