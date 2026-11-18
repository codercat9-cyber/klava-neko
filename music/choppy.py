"""Рваность пения: внутри фраз — где оригинальный голос звучит, а Клава молчит или «пропадает» тоном.

Друг 30.09 про первый русский кавер: «поёт глючно и не мелодично, отрывками». Разборчивость и попадание в ноты этого
не ловили — меряется то, что слышно как «отрывки»: дыры в звуке и в тоне посреди фразы и их длина.

    tts\\.venv\\Scripts\\python.exe music\\choppy.py <папка кавера> [guide.wav|vocal.wav]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import analyze as A  # noqa: E402
import common as C  # noqa: E402


def holes(mask: np.ndarray, fps: int = 100) -> list[float]:
    out, n = [], 0
    for v in mask:
        if v:
            n += 1
        elif n:
            out.append(n / fps)
            n = 0
    if n:
        out.append(n / fps)
    return out


def main() -> int:
    out = Path(sys.argv[1])
    which = sys.argv[2] if len(sys.argv) > 2 else "vocal.wav"
    info = json.loads((out / "cover.json").read_text(encoding="utf-8"))
    src = C.TRACKS / out.name.rsplit("-", 1)[0]
    if not src.exists():
        src = next(d for d in C.TRACKS.iterdir() if out.name.startswith(d.name))
    f0o = np.load(src / "vocal_f0.npy")
    import soundfile as sf
    w, sr = sf.read(str(out / which), dtype="float32")
    w = C.mono(w) if w.ndim > 1 else w
    f0k = A.vocal_f0(C.resample(w, sr, C.SR), C.SR)
    rk = A.frame_rms(C.resample(w, sr, C.SR), C.SR, 100)
    n = min(len(f0o), len(f0k), len(rk))
    thr = np.percentile(rk[rk > 0], 90) * 0.03 if np.any(rk > 0) else 1e-4
    tot_o = tot_sil = tot_unv = 0
    gaps: list[float] = []
    rows = []
    for l in info["lines"]:
        a, b = int(l["t0"] * 100), min(n, int(l["t1"] * 100))
        if b <= a:
            continue
        orig = f0o[a:b] > 0
        sil = orig & (rk[a:b] < thr)          # оригинал звучит, у неё тишина
        unv = orig & (f0k[a:b] <= 0)          # оригинал поёт тоном, у неё тона нет
        tot_o += orig.sum()
        tot_sil += sil.sum()
        tot_unv += unv.sum()
        g = [h for h in holes(unv) if h >= 0.06]
        gaps += g
        rows.append((l["text"][:40], round(float(sil.sum() / max(1, orig.sum())), 2), round(float(unv.sum() / max(1, orig.sum())), 2), len(g)))
    print(json.dumps({"file": which, "silent_where_orig_sings": round(tot_sil / max(1, tot_o), 3),
                      "no_pitch_where_orig_sings": round(tot_unv / max(1, tot_o), 3),
                      "holes_ge_60ms": len(gaps), "median_hole_s": round(float(np.median(gaps)), 3) if gaps else 0}, ensure_ascii=False))
    for r in rows:
        print(f"  тишина {r[1]:.2f} | без тона {r[2]:.2f} | дыр {r[3]:2d} | {r[0]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
