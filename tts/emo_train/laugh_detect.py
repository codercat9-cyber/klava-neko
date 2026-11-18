"""Слышен ли смех? Детектор через уши Клавы — whisper-сервер (одноразовая проверка).

Друг 28.09: «смеха не было» — длительность и тон (мои прежние замеры) смех не ловят. Whisper, если слышит смех,
пишет его словами («Ха-ха-ха», «(смеётся)», «[смех]»). Проверка на заведомо живом смехе (исходники ASVP) — потом на её
выдаче. Файлы — аргументами или маской.

    tts\\.venv\\Scripts\\python.exe emo_train\\laugh_detect.py файл.wav [маска*.wav ...]
"""
import glob
import io
import json
import re
import sys
import urllib.request
import uuid

import librosa
import numpy as np
import soundfile as sf

URL = "http://127.0.0.1:8097/inference"
LAUGH_RX = re.compile(r"(?:ха|хи|хе|ah|ha|he)(?:[-\s]?(?:ха|хи|хе|ah|ha|he)){1,}|смех|смеёт|смеет|laugh|хохо", re.IGNORECASE)


def transcribe(path: str) -> str:
    w, sr = sf.read(path, dtype="float32")
    if w.ndim > 1:
        w = w.mean(axis=1)
    w16 = librosa.resample(w, orig_sr=sr, target_sr=16000) if sr != 16000 else w
    buf = io.BytesIO()
    sf.write(buf, np.clip(w16, -1, 1), 16000, subtype="PCM_16", format="WAV")
    b = "----" + uuid.uuid4().hex
    parts = []
    for name, val in (("response_format", "json"), ("temperature", "0.0"), ("language", "ru")):
        parts.append(f"--{b}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{val}\r\n".encode())
    parts.append(f"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.wav\"\r\nContent-Type: audio/wav\r\n\r\n".encode()
                 + buf.getvalue() + b"\r\n")
    parts.append(f"--{b}--\r\n".encode())
    req = urllib.request.Request(URL, data=b"".join(parts), headers={"Content-Type": f"multipart/form-data; boundary={b}"})
    return json.loads(urllib.request.urlopen(req, timeout=60).read()).get("text", "").strip()


def main() -> int:
    files = [f for a in sys.argv[1:] for f in sorted(glob.glob(a))]
    hits = 0
    for f in files:
        t = transcribe(f)
        laugh = bool(LAUGH_RX.search(t))
        hits += laugh
        print(f"{'СМЕХ' if laugh else '    '} {f.split(chr(92))[-1].split('/')[-1]:28} | {t[:110]}", flush=True)
    print(f"смех слышен в {hits} из {len(files)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
