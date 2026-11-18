"""Слышна ли эмоция (нейросеть — только замеры). Судья — русский классификатор эмоций речи (HuBERT-large, дообучен на
Dusha: нейтрально / злость / позитив / грусть): прежний Aniemore на голосе Клавы глох («энтузиазм» почти всегда).
Сначала судья проверяется на живых актрисах корпуса Dialogs (какая доля угадана — потолок), потом слушает голос Клавы
через живой сервер: стиль с LoRA (lora) против манеры без неё (icl). Временные стили t_* в emotions.json — на время замера.

    tts\\.venv\\Scripts\\python.exe emo_train\\probe_ser.py [--url http://127.0.0.1:8096] [--n 5] [--styles angry,sad,happy]
"""
import argparse
import csv
import io
import json
import sys
import urllib.request
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
import torch
from transformers import AutoFeatureExtractor, HubertForSequenceClassification

TTS = Path(__file__).resolve().parent.parent
ROOT = TTS.parent
sys.path.insert(0, str(TTS))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from evaluate import TEXTS  # noqa: E402

EMO = TTS / "emotions.json"
SER = ROOT / "models" / "eval" / "ser-dusha"
DIALOGS = ROOT / "data" / "emotion" / "dialogs"
# стиль Клавы → класс Dusha
TARGET = {"angry": "angry", "annoyed": "angry", "sad": "sad", "happy": "positive", "laugh": "positive", "sly": "positive",
          "surprised": "positive", "calm": "neutral", "scared": "sad", "whisper": "neutral"}
DLG = {"angry": "angry", "sad": "sad", "happy": "positive", "neutral": "neutral"}


def post(url: str, path: str, body: dict | None = None) -> bytes:
    req = urllib.request.Request(url + path, data=json.dumps(body or {}).encode("utf-8"), headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=180).read()


class Judge:
    def __init__(self):
        self.fe = AutoFeatureExtractor.from_pretrained(str(SER))
        self.m = HubertForSequenceClassification.from_pretrained(str(SER)).eval().to("cuda")
        self.labels = self.m.config.id2label

    def __call__(self, wav: np.ndarray, sr: int) -> dict[str, float]:
        y = librosa.resample(wav.astype(np.float32), orig_sr=sr, target_sr=16000)
        x = self.fe(y, sampling_rate=16000, return_tensors="pt")
        with torch.no_grad():
            p = torch.softmax(self.m(**{k: v.to("cuda") for k, v in x.items()}).logits, dim=-1)[0].cpu().numpy()
        return {self.labels[i]: float(p[i]) for i in range(len(p))}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:8096")
    ap.add_argument("--n", type=int, default=5)
    ap.add_argument("--styles", default="angry,annoyed,sad,happy,sly,surprised")
    ap.add_argument("--modes", default="lora,icl")
    ap.add_argument("--out", default=str(TTS / "emo_train" / "probe_ser.json"))
    a = ap.parse_args()
    judge = Judge()
    # потолок: живые актрисы
    rows = [r for r in csv.DictReader(open(DIALOGS / "val.csv", encoding="utf-8"), delimiter="|") if r["speaker_id"] in "MS" and r["emotion"] in DLG]
    ceil = {}
    for emo, cls in DLG.items():
        ps, hits = [], 0
        for r in [x for x in rows if x["emotion"] == emo and (DIALOGS / x["audio_path"]).exists()][:8]:
            w, sr = sf.read(DIALOGS / r["audio_path"], dtype="float32")
            pr = judge(w, sr)
            ps.append(pr[cls])
            hits += int(max(pr, key=pr.get) == cls)
        ceil[emo] = (round(float(np.mean(ps)), 3), hits, len(ps))
        print(f"актрисы {emo:8}: P({cls}) {ceil[emo][0]:.2f}, угадано {hits}/{len(ps)}", flush=True)
    original = EMO.read_text(encoding="utf-8")
    cfg = json.loads(original)
    styles = [s for s in a.styles.split(",") if s in cfg]
    for s in styles:
        base = {k: v for k, v in cfg[s].items() if k in ("instruct", "ref", "ref_text", "loud_db")}
        cfg[f"t_{s}_lora"] = {**base, "lora": True}
        cfg[f"t_{s}_icl"] = base
    cfg["t_calm_icl"] = {k: v for k, v in cfg.get("calm", {}).items() if k in ("loud_db",)}
    EMO.write_text(json.dumps(cfg, ensure_ascii=False, indent=1), encoding="utf-8")
    out = {"actresses": ceil}
    try:
        post(a.url, "/reload")
        for s in ["calm"] + styles:
            cls = TARGET.get(s, "neutral")
            for mode in (["icl"] if s == "calm" else a.modes.split(",")):
                ps, hits = [], 0
                for t in TEXTS[: a.n]:
                    w, sr = sf.read(io.BytesIO(post(a.url, "/tts", {"text": t, "emotion": f"t_{s}_{mode}"})), dtype="float32")
                    pr = judge(w, sr)
                    ps.append(pr[cls])
                    hits += int(max(pr, key=pr.get) == cls)
                out.setdefault(s, {})[mode] = {"p": round(float(np.mean(ps)), 3), "hit": hits, "n": a.n}
                print(f"{s:9} {mode:4} P({cls}) {np.mean(ps):.2f}, угадано {hits}/{a.n}", flush=True)
        Path(a.out).write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    finally:
        EMO.write_text(original, encoding="utf-8")
        post(a.url, "/reload")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
