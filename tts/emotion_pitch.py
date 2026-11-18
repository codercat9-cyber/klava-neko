"""Эмоциональные эталоны Клавы — на её обычную высоту голоса (одноразовая обработка звука).

Друг 28.09: «слетает тембр с чуть грубоватого на лоляшный высокий тон». Эталоны из VoiceDesign (смех, грусть, смущение,
хитрость, раздражение, нежность) выше klava_02 на 1–4 полутона, а клон копирует и высоту эталона. Здесь каждый
эталон сдвигается по тону (librosa, темп не меняется) к медиане klava_02 × 0,95 — чуть ниже, «погрубее»; шёпот
не трогаем (у шёпота нет тона). Результат — <имя>_fix.wav рядом, tts/emotions.json переписывается на них.

    tts\\.venv\\Scripts\\python.exe emotion_pitch.py [--target 300]
"""
import argparse
import json
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf

from design_voice import median_f0

ROOT = Path(__file__).resolve().parent.parent
EMOTIONS = ROOT / "tts" / "emotions.json"
REF = ROOT / "tts" / "voices" / "candidates" / "klava_02.wav"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", type=float, default=0.0, help="Гц; 0 — медиана klava_02 × 0,95")
    a = ap.parse_args()
    ref, sr = sf.read(REF, dtype="float32")
    target = a.target or median_f0(ref, sr) * 0.95
    print(f"цель: {target:.0f} Гц (klava_02 {median_f0(ref, sr):.0f} Гц)")
    cfg = json.loads(EMOTIONS.read_text(encoding="utf-8"))
    for name, e in cfg.items():
        if name.startswith("_") or not isinstance(e, dict) or "ref" not in e or name == "whisper":
            continue
        src = ROOT / e["ref"].replace("_fix.wav", ".wav")
        wav, sr = sf.read(src, dtype="float32")
        f0 = median_f0(wav, sr)
        if f0 <= 0:
            print(f"{name}: тон не определился — оставляю как есть")
            continue
        steps = float(12 * np.log2(target / f0))
        fixed = librosa.effects.pitch_shift(wav, sr=sr, n_steps=steps) if abs(steps) > 0.2 else wav
        out = src.with_name(src.stem + "_fix.wav")
        sf.write(out, fixed, sr)
        e["ref"] = str(out.relative_to(ROOT)).replace("\\", "/")
        print(f"{name}: {f0:.0f} Гц → сдвиг {steps:+.2f} полутона → {median_f0(fixed, sr):.0f} Гц ({out.name})")
    EMOTIONS.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    print("emotions.json переписан")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
