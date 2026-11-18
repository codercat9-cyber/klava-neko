"""Живой звук по образцу друга — голосом Клавы (одноразовая сборка банка; нейросети — Python).

Друг 28.09: «ахегао со стонами — вот референс Yamete Kudasai, все стоны и ахегао делать на нём», озвучка текста
не живая. Образец (mp3/wav, любой голос) переводится в тембр Клавы тем же Seed-VC с тоном, что смех и плач
(convert.py): рисунок тона образца сохраняется, средний тон — её (и вариант на 2 полутона звонче). Несколько вариантов
глубины (шаги диффузии) и строгости (cfg) — друг выбирает на слух; тембр (WavLM) и тон пишутся в имя.

    tts\\.venv\\Scripts\\python.exe tts\\nonverbal\\from_ref.py образец.mp3 --name ahegao [--out папка] [--split]

--split — образец длинный, из нескольких звуков: режется по паузам (≥ 0,25 с тишины) на куски 0,4–4 с.
Итог: tts/voices/nonverbal/<name>_NN.wav + копии в --out (по умолчанию рядом с образцом) с понятными именами.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import tempfile
from pathlib import Path

import numpy as np
import soundfile as sf

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import convert  # noqa: E402  (Seed-VC, эталон Клавы, замер тона)
from convert import OUT, REF, Converter, median_f0, speaker_id, to16k  # noqa: E402

VARIANTS = [(30, 0.7, 0.0), (50, 0.7, 0.0), (30, 0.5, 0.0), (50, 0.9, 0.0), (30, 0.7, 2.0), (50, 0.7, 2.0)]


def pieces(w: np.ndarray, sr: int) -> list[np.ndarray]:
    """Куски по паузам: тишина ≥ 0,25 с (ниже 8 % громкости 95-го перцентиля) режет; куски короче 0,4 с — к соседу."""
    fr = int(sr * 0.02)
    n = len(w) // fr
    rms = np.sqrt(np.mean(w[:n * fr].reshape(n, fr) ** 2, axis=1))
    loud = rms > np.percentile(rms, 95) * 0.08
    out, start, quiet = [], None, 0
    for i, on in enumerate(loud):
        if on:
            if start is None:
                start = i
            quiet = 0
        elif start is not None:
            quiet += 1
            if quiet * 0.02 >= 0.25:
                out.append((start, i - quiet + 1))
                start, quiet = None, 0
    if start is not None:
        out.append((start, n))
    res: list[np.ndarray] = []
    for a, b in out:
        seg = w[max(0, a * fr - int(0.05 * sr)): min(len(w), b * fr + int(0.1 * sr))]
        if len(seg) < 0.4 * sr and res:
            res[-1] = np.concatenate([res[-1], seg])
        elif len(seg) <= 4 * sr:
            res.append(seg)
        else:
            res.extend(seg[i:i + 4 * sr] for i in range(0, len(seg), 4 * sr))
    return res


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("ref", help="образец: mp3/wav")
    ap.add_argument("--name", default="ahegao", help="имя банка (файлы <name>_NN.wav)")
    ap.add_argument("--out", default="", help="куда положить копии на прослушку (по умолчанию — рядом с образцом)")
    ap.add_argument("--split", action="store_true", help="резать длинный образец по паузам")
    a = ap.parse_args()
    import librosa
    src = Path(a.ref)
    w, sr = librosa.load(str(src), sr=44100, mono=True)
    parts = pieces(w, sr) if a.split else [w]
    print(f"образец {src.name}: {len(w) / sr:.2f} с, тон {median_f0(w, sr):.0f} Гц, кусков {len(parts)}", flush=True)
    speaker_id.load_encoder()
    rw, rs = sf.read(REF, dtype="float32")
    remb = speaker_id.embed_audio(to16k(rw, rs))
    listen = Path(a.out) if a.out else src.parent
    listen.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    conv: Converter | None = None
    rows = []
    with tempfile.TemporaryDirectory() as tmp:
        n = 0
        for pi, part in enumerate(parts):
            piece = Path(tmp) / f"piece_{pi}.wav"
            sf.write(piece, part, sr)
            for steps, cfg, semi in VARIANTS:
                if conv is None:
                    conv = Converter(steps, cfg)
                conv.steps, conv.cfg_rate, conv.semitones = steps, cfg, semi
                out = conv(piece)
                peak = float(np.max(np.abs(out))) or 1.0
                out = out / peak * 0.9
                e = speaker_id.embed_audio(to16k(out, conv.sr))
                sim = float(np.dot(remb, e) / (np.linalg.norm(remb) * np.linalg.norm(e) + 1e-9))
                f0 = median_f0(out, conv.sr)
                name = f"{a.name}_{n:02d}.wav"
                sf.write(OUT / name, out, conv.sr)
                pretty = f"{n + 1:02d} {'кусок ' + str(pi + 1) + ' ' if a.split else ''}шаги {steps} cfg {cfg} " \
                         f"{'звонче ' if semi else ''}тембр {sim:.2f} тон {f0:.0f}.wav"
                shutil.copyfile(OUT / name, listen / pretty)
                rows.append({"file": name, "src": src.name, "piece": pi, "steps": steps, "cfg": cfg, "semitones": semi,
                             "sim": round(sim, 3), "f0": round(f0), "dur": round(len(out) / conv.sr, 2)})
                print(json.dumps(rows[-1], ensure_ascii=False), flush=True)
                n += 1
    meta = OUT / "nonverbal.json"
    report = json.loads(meta.read_text(encoding="utf-8")) if meta.exists() else {}
    report[a.name] = rows
    meta.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"ГОТОВО: {len(rows)} вариантов → {listen}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
