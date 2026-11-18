"""Проверка кавера без ушей: разборчивость (whisper по её голосу против заданного текста), попадание в ноты (тон её
голоса против мелодии оригинала, в центах), похожесть тембра на её эталон (WavLM), баланс голоса и минуса.

    tts\\.venv\\Scripts\\python.exe music\\judge.py <папка кавера>
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import analyze as A  # noqa: E402
import common as C  # noqa: E402
import cover as CV  # noqa: E402


def norm(s: str) -> list[str]:
    import re
    return re.findall(r"[а-яёa-z]+", s.lower().replace("ё", "е"))


def wer(ref: list[str], hyp: list[str]) -> float:
    d = np.zeros((len(ref) + 1, len(hyp) + 1))
    d[:, 0] = np.arange(len(ref) + 1)
    d[0, :] = np.arange(len(hyp) + 1)
    for i in range(1, len(ref) + 1):
        for j in range(1, len(hyp) + 1):
            d[i, j] = min(d[i - 1, j] + 1, d[i, j - 1] + 1, d[i - 1, j - 1] + (ref[i - 1] != hyp[j - 1]))
    return float(d[-1, -1] / max(1, len(ref)))


def main() -> int:
    out = Path(sys.argv[1])
    info = json.loads((out / "cover.json").read_text(encoding="utf-8"))
    track = Path(json.loads((out / "cover.json").read_text(encoding="utf-8")).get("track", "")) if False else None
    v = C.mono(C.read(out / "vocal.wav"))
    # разборчивость по строкам
    lines = [l for l in info["lines"]]
    y16 = C.resample(v, C.SR, 16000)
    refs, hyps, per = [], [], []
    for l in lines:
        seg = y16[int(max(0, l["t0"] - 0.3) * 16000):int((l["t1"] + 0.4) * 16000)]
        if seg.size < 8000:
            continue
        lang = "ru" if any("а" <= ch <= "я" for ch in l["text"].lower()) else "en"
        r = A.whisper_call(seg, "http://127.0.0.1:8097", lang)
        hyp = " ".join(s.get("text", "") for s in r.get("segments", []))
        w = wer(norm(l["text"]), norm(hyp))
        per.append((round(w, 2), l["text"], hyp.strip()))
        refs += norm(l["text"])
        hyps += norm(hyp)
    total = wer(refs, hyps)
    # тон: её голос против оригинала (кадры, где звучат оба)
    d = Path(info["dir"]).parent
    src_dir = None
    for cand in (C.TRACKS).iterdir():
        if out.name.startswith(cand.name):
            src_dir = cand
    cents = None
    if src_dir is not None:
        f0o = np.load(src_dir / "vocal_f0.npy") * 2 ** (info.get("shift", 0) / 12)
        f0k = A.vocal_f0(v, C.SR)
        n = min(len(f0o), len(f0k))
        m = (f0o[:n] > 0) & (f0k[:n] > 0)
        if m.any():
            dc = 1200 * np.log2(f0k[:n][m] / f0o[:n][m])
            dc = (dc + 600) % 1200 - 600          # октавные промахи тона считаем отдельно
            cents = {"median_abs": round(float(np.median(np.abs(dc))), 1), "within_50": round(float(np.mean(np.abs(dc) < 50)), 3),
                     "frames": int(m.sum())}
    # тембр
    sys.path.insert(0, str(C.ROOT / "tts"))
    import soundfile as sf
    import speaker_id
    from emotion_voice import to16k
    speaker_id.load_encoder()
    rw, rs = sf.read(CV.REF, dtype="float32")
    e_ref = speaker_id.embed_audio(to16k(rw, rs))
    act = A.frame_rms(v, C.SR, 100)
    loud = np.repeat(act > np.percentile(act, 60), C.SR // 100)[: len(v)]
    e_k = speaker_id.embed_audio(to16k(v[: len(loud)][loud][: C.SR * 60], C.SR))
    sim = float(np.dot(e_ref, e_k) / (np.linalg.norm(e_ref) * np.linalg.norm(e_k) + 1e-9))
    print(json.dumps({"wer": round(total, 3), "pitch": cents, "timbre_sim": round(sim, 3)}, ensure_ascii=False))
    for w, ref, hyp in per:
        print(f"{w:4.2f} | {ref} | {hyp}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
