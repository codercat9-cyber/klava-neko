"""Влить адаптер ударений (LoRA v3, olluorg/voicy ADR 0023) в веса Qwen3-TTS-12Hz-1.7B-Base.

    python tts/merge_stress_lora.py [--base models/Qwen3-TTS-12Hz-1.7B-Base] [--lora models/stress_lora]
                                    [--out models/Qwen3-TTS-12Hz-1.7B-Base-ru-stress]

Меняются только матрицы talker.model.layers.* (W += B·A·alpha/r, считается в fp32, хранится в исходном bf16),
остальные файлы копируются как есть: та же форма и та же скорость, грузится так же, как официальные веса.
"""
import argparse
import json
import shutil
import sys
from pathlib import Path

import torch
from safetensors import safe_open
from safetensors.torch import load_file, save_file

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=str(ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base"))
    ap.add_argument("--lora", default=str(ROOT / "models" / "stress_lora"))
    ap.add_argument("--out", default=str(ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress"))
    a = ap.parse_args()
    base, lora_dir, out = Path(a.base), Path(a.lora), Path(a.out)
    cfg = json.loads((lora_dir / "adapter.json").read_text(encoding="utf-8"))
    scale = cfg["alpha"] / cfg["r"]
    pairs: dict[str, dict[str, torch.Tensor]] = {}
    for k, v in load_file(str(lora_dir / "adapter.safetensors")).items():
        stem, part = k.rsplit(".lora_", 1)
        pairs.setdefault(stem, {})[part.split(".")[0]] = v

    out.mkdir(parents=True, exist_ok=True)
    for p in base.rglob("*"):
        if p.is_file() and p.name != "model.safetensors" or p.is_file() and p.parent != base:
            dst = out / p.relative_to(base)
            dst.parent.mkdir(parents=True, exist_ok=True)
            if not dst.exists():
                shutil.copy2(p, dst)

    weights = {}
    with safe_open(str(base / "model.safetensors"), "pt") as f:
        meta = f.metadata()
        for k in f.keys():
            weights[k] = f.get_tensor(k)
    merged = 0
    for stem, ab in pairs.items():
        key = f"talker.model.{stem}.weight"
        if key not in weights:
            print(f"нет матрицы {key} — адаптер не от этой модели", file=sys.stderr)
            return 1
        w = weights[key]
        delta = (ab["B"].float() @ ab["A"].float()) * scale
        if delta.shape != w.shape:
            print(f"{key}: форма {tuple(delta.shape)} против {tuple(w.shape)}", file=sys.stderr)
            return 1
        weights[key] = (w.float() + delta).to(w.dtype)
        merged += 1
    save_file(weights, str(out / "model.safetensors"), metadata=meta)
    (out / "merge.json").write_text(json.dumps({"base": str(base), "adapter": str(lora_dir), "merged_matrices": merged,
                                                "scale": scale}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"влито матриц: {merged}, масштаб {scale} → {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
