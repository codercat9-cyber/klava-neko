"""Сколько срывов у эмоций с LoRA (нейросеть — замеры через живой сервер). Срыв — то, что сервер сам чинит повторным
синтезом: тишина вместо речи, «не остановилась», тон выше потолка. У потока повторить нельзя (начало уже звучит), поэтому
эмоции с LoRA идут потоком, только если срывов мало. Временные стили t_*_lora — на время замера.

    tts\\.venv\\Scripts\\python.exe emo_train\\probe_fail.py [--url http://127.0.0.1:8096] [--n 8]
"""
import argparse
import io
import json
import sys
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf

TTS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TTS))
from server import MAX_F0, Take  # noqa: E402

EMO = TTS / "emotions.json"
TEXTS = [
    "Я не знаю, что ты сейчас скажешь, но я слушаю.", "Сегодня мы опять засиделись допоздна.", "Ты правда это сделал сам?",
    "Ну вот, опять всё начинается сначала.", "Давай посмотрим, что там получилось.", "Я только что вернулась домой и сразу к тебе.",
    "Слушай, это же просто невероятно, как у тебя вышло!", "Тс-с, не шуми, все уже спят.", "Пфф, да ладно, не может быть.",
    "Короче, я тебе сейчас всё объясню по порядку.", "Мур, как же хорошо сидеть вот так вместе.", "Что это вообще было, а?",
]


def post(url: str, path: str, body: dict | None = None) -> bytes:
    req = urllib.request.Request(url + path, data=json.dumps(body or {}).encode("utf-8"), headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=180).read()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:8096")
    ap.add_argument("--n", type=int, default=12)
    ap.add_argument("--styles", default="angry,annoyed,whisper,scared,happy,sly,surprised,sad")
    a = ap.parse_args()
    original = EMO.read_text(encoding="utf-8")
    cfg = json.loads(original)
    styles = [s for s in a.styles.split(",") if s in cfg]
    for s in styles:
        base = {k: v for k, v in cfg[s].items() if k in ("instruct", "ref", "ref_text", "loud_db", "max_f0")}
        # сервер чинит срыв повтором — здесь меряем сырой первый дубль: max_f0 огромный, повтор отключается ниже
        cfg[f"t_{s}_lora"] = {**base, "lora": True}
    EMO.write_text(json.dumps(cfg, ensure_ascii=False, indent=1), encoding="utf-8")
    out = {}
    try:
        post(a.url, "/reload")
        for s in styles:
            bad, severe, f0s = 0, 0, []
            for t in TEXTS[: a.n]:
                w, sr = sf.read(io.BytesIO(post(a.url, "/tts", {"text": t, "emotion": f"t_{s}_lora"})), dtype="float32")
                take = Take(w, sr, len(t), len(t) / 8 + 1.5, cfg[s].get("max_f0", MAX_F0))
                bad += int(bool(take.bad))
                severe += take.severe
                f0s.append(take.f0)
            out[s] = {"bad": bad, "severe": severe, "n": a.n, "f0": round(float(np.median(f0s)))}
            print(f"{s:9}: итог после починки сервером — плохих {bad}/{a.n}, тяжёлых {severe}, тон {np.median(f0s):.0f} Гц", flush=True)
        (TTS / "emo_train" / "probe_fail.json").write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    finally:
        EMO.write_text(original, encoding="utf-8")
        post(a.url, "/reload")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
