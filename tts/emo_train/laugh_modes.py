"""Смех по метке 🤣: какой режим его реально даёт — «только отпечаток» (как учили) или клон с эталоном (ICL + LoRA).
Через живой сервер (временные стили t_*), судья — whisper (laugh_detect.py). Одноразовая проверка.

    tts\\.venv\\Scripts\\python.exe emo_train\\laugh_modes.py
"""
import io
import json
import sys
import urllib.request
from pathlib import Path

import soundfile as sf

TTS = Path(__file__).resolve().parent.parent
ROOT = TTS.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from laugh_detect import LAUGH_RX, transcribe  # noqa: E402

URL = "http://127.0.0.1:8095"
EMO = TTS / "emotions.json"
OUT = ROOT / "web" / "voices" / "emotions" / "laugh2" / "modes"
TEXTS = ["🤣 🤣 🤣", "Ну ты выдал 🤣 🤣, Кибер Кот!", "🤣 Ладно, признаю, это было смешно 🤣."]


def post(path: str, body: dict | None = None) -> bytes:
    req = urllib.request.Request(URL + path, data=json.dumps(body or {}).encode("utf-8"), headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=180).read()


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    original = EMO.read_text(encoding="utf-8")
    cfg = json.loads(original)
    manner = cfg["laugh"]["instruct"]
    cfg["t_laugh_xvec"] = {"instruct": manner, "xvec": True, "max_f0": 520}
    cfg["t_laugh_lora"] = {"instruct": manner, "lora": True, "max_f0": 520}
    EMO.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    try:
        post("/reload")
        for mode in ("t_laugh_xvec", "t_laugh_lora"):
            hits = 0
            n = 0
            for i, t in enumerate(TEXTS):
                for rep in range(2):
                    w = post("/tts", {"text": t, "emotion": mode})
                    path = OUT / f"{mode}_{i}_{rep}.wav"
                    path.write_bytes(w)
                    tr = transcribe(str(path))
                    laugh = bool(LAUGH_RX.search(tr))
                    hits += laugh
                    n += 1
                    wav, sr = sf.read(io.BytesIO(w), dtype="float32")
                    print(f"{mode:13} {'СМЕХ' if laugh else '    '} {len(wav) / sr:4.1f} с | {t[:30]:30} → {tr[:70]}", flush=True)
            print(f"{mode}: смех слышен в {hits} из {n}", flush=True)
    finally:
        EMO.write_text(original, encoding="utf-8")
        post("/reload")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
