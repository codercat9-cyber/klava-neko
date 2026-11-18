"""Влить эмоциональную LoRA (emo_train/train.py) в веса голоса Клавы.

    tts\\.venv\\Scripts\\python.exe emo_train\\merge.py [--base models/Qwen3-TTS-12Hz-1.7B-Base-ru-stress]
                                                        [--out models/Qwen3-TTS-12Hz-1.7B-Base-ru-stress-emo]

W += B·A·alpha/r (fp32, хранится в исходном bf16) — и в слоях talker, и в предсказателе хвоста кода (ключи адаптера
от корня talker: «model.layers.N…», «code_predictor.model.layers.N…»). Остальное копируется как есть: форма, скорость
и CUDA-графы faster-qwen3-tts те же. Файл merge.json с ударениями (признак «понимает знаки») сохраняется.
"""
import argparse
import json
import shutil
import sys
from pathlib import Path

import torch
from safetensors import safe_open
from safetensors.torch import load_file, save_file

ROOT = Path(__file__).resolve().parents[2]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=str(ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress"))
    ap.add_argument("--lora", default=str(ROOT / "models" / "emo_lora"))
    ap.add_argument("--out", default=str(ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress-emo"))
    a = ap.parse_args()
    base, lora_dir, out = Path(a.base), Path(a.lora), Path(a.out)
    cfg = json.loads((lora_dir / "adapter.json").read_text(encoding="utf-8"))
    scale = cfg["alpha"] / cfg["rank"]
    prefix = cfg.get("prefix", "talker.")
    pairs: dict[str, dict[str, torch.Tensor]] = {}
    for k, v in load_file(str(lora_dir / "adapter.safetensors")).items():
        stem, part = k.rsplit(".lora_", 1)
        pairs.setdefault(stem, {})[part.split(".")[0]] = v

    out.mkdir(parents=True, exist_ok=True)
    for p in base.rglob("*"):
        if p.is_file() and not (p.name == "model.safetensors" and p.parent == base):
            dst = out / p.relative_to(base)
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, dst)

    weights = {}
    with safe_open(str(base / "model.safetensors"), "pt") as f:
        meta = f.metadata()
        for k in f.keys():
            weights[k] = f.get_tensor(k)
    merged = 0
    for stem, ab in pairs.items():
        key = f"{prefix}{stem}.weight"
        if key not in weights or "A" not in ab or "B" not in ab:
            print(f"нет матрицы {key} (или половины адаптера) — адаптер не от этой модели", file=sys.stderr)
            return 1
        w = weights[key]
        delta = (ab["B"].float() @ ab["A"].float()) * scale
        if delta.shape != w.shape:
            print(f"{key}: форма {tuple(delta.shape)} против {tuple(w.shape)}", file=sys.stderr)
            return 1
        weights[key] = (w.float() + delta).to(w.dtype)
        merged += 1
    save_file(weights, str(out / "model.safetensors"), metadata=meta)
    info = json.loads((base / "merge.json").read_text(encoding="utf-8")) if (base / "merge.json").exists() else {}
    info["emotion_adapter"] = {"adapter": str(lora_dir), "merged_matrices": merged, "scale": scale, "epoch": cfg.get("epoch"),
                               "styles": cfg.get("styles")}
    (out / "merge.json").write_text(json.dumps(info, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"влито матриц: {merged}, масштаб {scale} → {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
