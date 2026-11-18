"""Понижение тона эталона голоса Клавы (одноразовая обработка звука).

Qwen3-TTS клонирует тембр и высоту эталона, поэтому ниже эталон — ниже и её речь.
librosa.effects.pitch_shift сдвигает тон, не меняя темп; вместе с тоном немного опускаются и форманты —
на 3–4 полутона это звучит как голос постарше и «побольше», а не как замедленная плёнка.

    python pitch_voice.py klava_02 klava_04 klava_06 --steps -3 -4
Результат: tts/voices/candidates/<имя>_m3.wav и копия в web/voices для прослушивания.
"""
import argparse
import shutil
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf

from design_voice import median_f0

ROOT = Path(__file__).resolve().parent.parent
CAND = ROOT / "tts" / "voices" / "candidates"
WEB = ROOT / "web" / "voices"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="+")
    ap.add_argument("--steps", nargs="+", type=float, default=[-3.0, -4.0])
    args = ap.parse_args()
    for name in args.names:
        wav, sr = sf.read(CAND / f"{name}.wav", dtype="float32")
        if wav.ndim > 1:
            wav = wav.mean(axis=1)
        for st in args.steps:
            out = librosa.effects.pitch_shift(wav, sr=sr, n_steps=st, res_type="soxr_hq")
            out = out / max(1e-6, float(np.max(np.abs(out)))) * 0.9       # громкость как у оригинала, без клиппинга
            tag = f"{name}_m{abs(int(st))}"
            sf.write(CAND / f"{tag}.wav", out, sr, subtype="PCM_16")
            shutil.copyfile(CAND / f"{tag}.wav", WEB / f"{tag}.wav")
            print(f"{tag}: тон {median_f0(wav, sr):.0f} -> {median_f0(out, sr):.0f} Гц", flush=True)


if __name__ == "__main__":
    main()
