"""Общее для музыкального слуха и пения Клавы: пути, чтение звука через ffmpeg, запись, выбор видеокарты, громкость.

Лишние пакеты (demucs, beat_this, basic-pitch, pyworld, pretty_midi) лежат в music/_pkgs — В КОНЦЕ пути поиска:
torch, numpy и прочее берутся только из окружения голоса (tts/.venv), его версии не трогаются.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Callable

import numpy as np
import soundfile as sf

ROOT = Path(__file__).resolve().parents[1]
MUSIC = ROOT / "music"
MODELS = ROOT / "models" / "music"
DATA = ROOT / "data" / "music"
TRACKS = DATA / "tracks"
AST_DIR = ROOT / "models" / "audio" / "ast-audioset"
PHONE_DIR = ROOT / "models" / "eval" / "w2v2-phoneme"
SEED = ROOT / "tools" / "seed-vc"
SR = 44100

# веса demucs и beat_this качаются через torch.hub — пусть лежат рядом с остальными моделями, а не в профиле
os.environ.setdefault("TORCH_HOME", str(MODELS / "torch"))
if str(MUSIC / "_pkgs") not in sys.path:
    sys.path.append(str(MUSIC / "_pkgs"))

Progress = Callable[[float, str], None]


def log(msg: str) -> None:
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def no_progress(_p: float, msg: str) -> None:
    log(msg)


def decode(path: str | Path, sr: int = SR, seconds: float | None = None) -> np.ndarray:
    """Любой файл, который понимает ffmpeg (mp3, ogg, flac, m4a, видео) → float32 [n, 2] на частоте sr."""
    cmd = ["ffmpeg", "-v", "error", "-nostdin", "-i", str(path)]
    if seconds:
        cmd += ["-t", f"{seconds:.3f}"]
    cmd += ["-vn", "-ac", "2", "-ar", str(sr), "-f", "f32le", "-"]
    r = subprocess.run(cmd, capture_output=True, check=False)
    if r.returncode != 0 or not r.stdout:
        raise RuntimeError(f"ffmpeg не прочитал {path}: {r.stderr.decode('utf-8', 'replace').strip()[:300]}")
    return np.frombuffer(r.stdout, dtype=np.float32).reshape(-1, 2).copy()


def tags(path: str | Path) -> dict[str, str]:
    """Название и исполнитель из тегов файла (ffprobe); нет — пусто."""
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format_tags=title,artist,album:stream_tags=title,artist",
                        "-of", "json", str(path)], capture_output=True, check=False)
    out: dict[str, str] = {}
    try:
        info = json.loads(r.stdout.decode("utf-8", "replace") or "{}")
    except json.JSONDecodeError:
        return out
    for src in [info.get("format", {}).get("tags", {})] + [s.get("tags", {}) for s in info.get("streams", [])]:
        for k, v in (src or {}).items():
            k = k.lower()
            if k in ("title", "artist", "album") and v and k not in out:
                out[k] = str(v).strip()
    return out


def write(path: Path, wav: np.ndarray, sr: int = SR) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    peak = float(np.max(np.abs(wav))) if wav.size else 0.0
    if peak > 1.0:
        wav = wav / peak * 0.999
    fmt = "FLAC" if path.suffix.lower() == ".flac" else "WAV"
    sf.write(str(path), wav, sr, format=fmt, subtype="PCM_16")
    return path


def read(path: Path, sr: int = SR) -> np.ndarray:
    w, s = sf.read(str(path), dtype="float32", always_2d=True)
    if s != sr:
        import librosa
        w = librosa.resample(w.T, orig_sr=s, target_sr=sr).T.astype(np.float32)
    return w


def mono(w: np.ndarray) -> np.ndarray:
    return w.mean(axis=1) if w.ndim > 1 else w


def resample(x: np.ndarray, sr_in: int, sr_out: int) -> np.ndarray:
    if sr_in == sr_out:
        return x.astype(np.float32)
    import soxr
    return soxr.resample(x, sr_in, sr_out).astype(np.float32)


def slug(name: str) -> str:
    s = re.sub(r"[^\w\-]+", "_", name, flags=re.UNICODE).strip("_")
    return (s or "track")[:48]


def track_dir(path: str | Path) -> Path:
    """Папка разбора трека: имя + отпечаток (путь, размер, время) — тот же файл второй раз не разбирается."""
    p = Path(path).resolve()
    st = p.stat()
    h = hashlib.sha1(f"{p}|{st.st_size}|{int(st.st_mtime)}".encode("utf-8")).hexdigest()[:10]
    return TRACKS / f"{slug(p.stem)}-{h}"


def device(need_gb: float) -> str:
    """Видеокарта, если на ней свободно хотя бы need_gb (там же живут голос, слух и мозг Клавы), иначе процессор."""
    import torch
    if not torch.cuda.is_available():
        return "cpu"
    try:
        free, _ = torch.cuda.mem_get_info()
    except RuntimeError:
        return "cpu"
    return "cuda" if free / 2**30 >= need_gb else "cpu"


def free_gpu() -> None:
    import gc
    gc.collect()
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


# ---------- громкость по ITU-R BS.1770 (K-взвешивание, стробирование) ----------

def _k_filter(x: np.ndarray, sr: int) -> np.ndarray:
    from scipy.signal import lfilter
    # полка +4 дБ на верхах (голова) и срез низа 38 Гц — коэффициенты как в libebur128 для любой частоты дискретизации
    f0, g, q = 1681.974450955533, 3.999843853973347, 0.7071752369554196
    k = np.tan(np.pi * f0 / sr)
    vh = 10 ** (g / 20)
    vb = vh ** 0.4996667741545416
    a0 = 1 + k / q + k * k
    b1 = [(vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0]
    a1 = [1.0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0]
    f1, q1 = 38.13547087602444, 0.5003270373238773
    k = np.tan(np.pi * f1 / sr)
    a0 = 1 + k / q1 + k * k
    b2 = [1.0, -2.0, 1.0]
    a2 = [1.0, 2 * (k * k - 1) / a0, (1 - k / q1 + k * k) / a0]
    y = lfilter(b1, a1, x, axis=0)
    return lfilter(b2, a2, y, axis=0)


def loudness(w: np.ndarray, sr: int = SR) -> tuple[float, np.ndarray]:
    """(интегральная громкость LUFS, кратковременная громкость по окнам 3 с с шагом 1 с)."""
    x = w if w.ndim > 1 else w[:, None]
    y = _k_filter(x.astype(np.float64), sr)
    p = (y ** 2).sum(axis=1)
    blk, hop = int(0.4 * sr), int(0.1 * sr)
    n = max(0, (len(p) - blk) // hop + 1)
    if n == 0:
        return -70.0, np.zeros(0)
    cs = np.concatenate([[0.0], np.cumsum(p)])
    ms = (cs[np.arange(n) * hop + blk] - cs[np.arange(n) * hop]) / blk
    lk = -0.691 + 10 * np.log10(ms + 1e-12)
    gated = ms[lk > -70]
    if gated.size == 0:
        return -70.0, np.zeros(0)
    rel = -0.691 + 10 * np.log10(gated.mean()) - 10
    final = ms[(lk > -70) & (lk > rel)]
    integ = float(-0.691 + 10 * np.log10(final.mean() + 1e-12)) if final.size else -70.0
    sblk, shop = 3 * sr, sr
    ns = max(0, (len(p) - sblk) // shop + 1)
    st = np.array([-0.691 + 10 * np.log10((cs[i * shop + sblk] - cs[i * shop]) / sblk + 1e-12) for i in range(ns)])
    return integ, st


def save_json(path: Path, obj) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=1), encoding="utf-8")
    tmp.replace(path)


NOTE_NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]


def note_name(midi: float) -> str:
    m = int(round(midi))
    return f"{NOTE_NAMES[m % 12]}{m // 12 - 1}"


def hz_to_midi(f: np.ndarray) -> np.ndarray:
    out = np.zeros_like(f, dtype=np.float64)
    v = f > 1
    out[v] = 69 + 12 * np.log2(f[v] / 440.0)
    return out


def midi_to_hz(m: np.ndarray) -> np.ndarray:
    out = np.zeros_like(m, dtype=np.float64)
    v = m > 0
    out[v] = 440.0 * 2 ** ((m[v] - 69) / 12)
    return out


def fmt_time(t: float) -> str:
    t = max(0.0, t)
    return f"{int(t // 60)}:{t % 60:04.1f}"
