"""Проба через живой сервер голоса: обученная эмоциональная LoRA поверх клона С ЭТАЛОНОМ (ICL) против «только отпечатка»
(xvec) и против манеры без LoRA (нейросеть — только замеры; синтез делает server.py, второй копии модели в видеопамяти нет —
первая версия пробы с отдельной моделью забила 12 ГБ и душила живую Клаву).

Друг 28.09: «иногда голос робот прорывается». Обученные стили говорили в режиме «только отпечаток» — без живого эталона
тембр беднее. Если LoRA поверх ICL (эталон klava_02 + манера, стиль с "lora": true) даёт ту же эмоцию, а тембр полный —
робот уходит без переобучения. Меряется: близость тембра (WavLM), тон, что слышит классификатор эмоций (Aniemore).
Временные стили t_* добавляются в tts/emotions.json и убираются в конце.

    tts\\.venv\\Scripts\\python.exe emo_train\\probe_icl.py [--n 4] [--url http://127.0.0.1:8095]
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
sys.path.insert(0, str(Path(__file__).resolve().parent))
import speaker_id  # noqa: E402
from emotion_voice import REF, to16k  # noqa: E402
from evaluate import TARGET, TEXTS, classify, load_ser  # noqa: E402
from server import median_f0  # noqa: E402

EMO = TTS / "emotions.json"
LORA = TTS.parent / "models" / "emo_lora" / "adapter.json"


def post(url: str, path: str, body: dict | None = None) -> bytes:
    req = urllib.request.Request(url + path, data=json.dumps(body or {}).encode("utf-8"), headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=180).read()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--n", type=int, default=4)
    ap.add_argument("--url", default="http://127.0.0.1:8095")
    ap.add_argument("--styles", default="angry,scared,whisper,happy,sad")
    ap.add_argument("--lora", default=str(LORA), help="adapter.json той LoRA, что загружена в сервер (--emo-lora)")
    ap.add_argument("--out", default=str(TTS / "emo_train" / "probe_icl.json"))
    ap.add_argument("--modes", default="xvec,lora,icl")
    a = ap.parse_args()
    trained = {k: v for k, v in json.loads(Path(a.lora).read_text(encoding="utf-8"))["styles"] if v}
    styles = [s for s in a.styles.split(",") if s in trained]
    original = EMO.read_text(encoding="utf-8")
    cfg = json.loads(original)
    for s in styles:
        cfg[f"t_{s}_xvec"] = {"instruct": trained[s], "xvec": True}
        cfg[f"t_{s}_lora"] = {"instruct": trained[s], "lora": True}
        cfg[f"t_{s}_icl"] = {"instruct": trained[s]}
    EMO.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    try:
        post(a.url, "/reload")
        speaker_id.load_encoder()
        rw, rs = sf.read(REF, dtype="float32")
        remb = speaker_id.embed_audio(to16k(rw, rs))
        ser = load_ser()
        out = {}
        for s in styles:
            target = TARGET.get(s)
            for mode in a.modes.split(","):
                sims, f0s, ps, hits = [], [], [], 0
                for t in TEXTS[: a.n]:
                    w, sr = sf.read(io.BytesIO(post(a.url, "/tts", {"text": t, "emotion": f"t_{s}_{mode}"})), dtype="float32")
                    e = speaker_id.embed_audio(to16k(w, sr))
                    sims.append(float(np.dot(remb, e) / (np.linalg.norm(remb) * np.linalg.norm(e) + 1e-9)))
                    f0s.append(median_f0(w, sr))
                    if target:
                        pr = classify(ser, w, sr)
                        ps.append(pr.get(target, 0.0))
                        hits += int(max(pr, key=pr.get) == target)
                row = {"sim": round(float(np.mean(sims)), 3), "sim_min": round(float(np.min(sims)), 3), "f0": round(float(np.median(f0s))),
                       "p": round(float(np.mean(ps)), 3) if ps else None, "hit": hits}
                out.setdefault(s, {})[mode] = row
                print(f"{s:8} {mode:5} тембр {row['sim']:.3f} (мин {row['sim_min']:.2f}) тон {row['f0']:4} P(цель) {row['p']} попаданий {hits}/{a.n}", flush=True)
        Path(a.out).write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    finally:
        EMO.write_text(original, encoding="utf-8")
        post(a.url, "/reload")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
