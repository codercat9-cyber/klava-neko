"""Слух Клавы: трек раскладывается на физику, а не угадывается «по ощущению».

Друг 30.09: «нейросети плохо слушают музыку — ритма не чувствуют, ни Моцарта, ни Сальери не заценят; разобрать трек
в код и обратно, чтобы она точно сказала, что звучит гитара, пианино или барабан, почувствовала ритм и могла
воспроизвести похожее». Сырой звук языковой модели бесполезен — она получает измеренную партитуру:

  1. стемы — Demucs htdemucs_ft: голос, ударные, бас, остальное (гитары, клавиши, струнные);
  2. доли и такты — beat_this (CPJKU 2024): темп, его гибкость (рубато), размер;
  3. ударные в события — бочка / малый / хэт по полосам спектра стема ударных, сетка 16-х на такт, свинг,
     оттяжка малого в мс (грув);
  4. ноты — голос: RMVPE (тон по 10 мс) → ноты; бас и остальное — Basic Pitch (Spotify, полифония);
  5. гармония — хрома по долям, тональность (профили Крумхансла), аккорды (шаблоны + бас + Витерби);
  6. форма — спектральная кластеризация повторов (McFee & Ellis 2014), имена частей по голосу и громкости;
  7. инструменты, жанр, настроение, тип голоса — AST AudioSet по окнам микса и каждого стема, с вероятностями;
  8. текст — whisper-server Клавы по чистому голосу, слова с таймкодами;
  9. звук — громкость LUFS, динамика, яркость, низ, ширина стерео, доли стемов.

Итог: analysis.json (всё), passport.txt (паспорт для мозга), score.txt (партитура по тактам — «трек в токенах»),
transcription.mid (MIDI всех партий), стемы во FLAC для студии.

    tts\\.venv\\Scripts\\python.exe music\\analyze.py <файл> [--quick] [--lang en]
"""
from __future__ import annotations

import argparse
import io
import json
import sys
import time
import urllib.request
import uuid
from collections import Counter
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import common as C  # noqa: E402

STEMS = ["vocals", "drums", "bass", "other"]
VERSION = 8

# ---------- модели (грузятся один раз на процесс сервера) ----------
_demucs = None
_beats = None
_rmvpe = None
_ast = None
_bp = None


def separate(mix: np.ndarray, progress: C.Progress) -> dict[str, np.ndarray]:
    """Demucs htdemucs_ft: четыре модели, каждая лучше всех в своём стеме. На видеокарте ~1,5 ГБ, иначе процессор."""
    import torch
    from demucs.apply import apply_model
    from demucs.pretrained import get_model
    global _demucs
    if _demucs is None:
        progress(0.02, "качаю/гружу разделитель на дорожки (Demucs)…")
        _demucs = get_model("htdemucs_ft")
        _demucs.eval()
    dev = C.device(2.0)
    x = torch.from_numpy(np.ascontiguousarray(mix.T))
    ref = x.mean(0)
    mu, sd = ref.mean(), ref.std() + 1e-8
    x = (x - mu) / sd
    subs = getattr(_demucs, "models", [_demucs])
    weights = getattr(_demucs, "weights", [[1.0] * len(_demucs.sources)] * len(subs))
    acc = torch.zeros(len(_demucs.sources), 2, x.shape[1])
    tot = torch.zeros(len(_demucs.sources))
    for i, (sub, w) in enumerate(zip(subs, weights)):
        progress(0.04 + 0.22 * i / len(subs), f"делю на дорожки: модель {i + 1}/{len(subs)} ({'видеокарта' if dev == 'cuda' else 'процессор'})")
        with torch.no_grad():
            out = apply_model(sub, x[None], device=dev, shifts=1, split=True, overlap=0.25, progress=False)[0].cpu()
        sub.cpu()
        wt = torch.tensor(w, dtype=torch.float32)
        acc += out * wt[:, None, None]
        tot += wt
        C.free_gpu()
    est = acc / tot[:, None, None] * sd + mu
    return {name: est[i].T.numpy().astype(np.float32) for i, name in enumerate(_demucs.sources)}


def beats_of(y: np.ndarray, sr: int) -> tuple[np.ndarray, np.ndarray]:
    """Доли и сильные доли (beat_this, без DBN — он требует madmom): секунды."""
    from beat_this.inference import Audio2Beats
    global _beats
    dev = C.device(1.0)
    if _beats is None or not str(_beats.device).startswith(dev):
        _beats = Audio2Beats(checkpoint_path="final0", device=dev, float16=False, dbn=False)
    b, d = _beats(y.astype(np.float32), sr)
    return np.asarray(b, dtype=np.float64), np.asarray(d, dtype=np.float64)


# ---------- темп, размер, такты ----------

def bars_of(beats: np.ndarray, downbeats: np.ndarray, duration: float) -> tuple[list[list[float]], int]:
    """Такты: списки долей от сильной до следующей сильной; размер — самое частое число долей в такте."""
    if len(beats) < 4:
        return [], 4
    idx = [int(np.argmin(np.abs(beats - d))) for d in downbeats if np.min(np.abs(beats - d)) < 0.08]
    idx = sorted(set(idx))
    if len(idx) < 2:
        idx = list(range(0, len(beats), 4))
    counts = Counter(b - a for a, b in zip(idx, idx[1:]) if 2 <= b - a <= 7)
    meter = counts.most_common(1)[0][0] if counts else 4
    bars: list[list[float]] = []
    # затакт до первой сильной доли — неполный такт
    if idx[0] > 0:
        bars.append(list(beats[: idx[0]]))
    for a, b in zip(idx, idx[1:] + [len(beats)]):
        seg = list(beats[a:b])
        # пропущенная сильная доля: длинный «такт» режется по размеру
        while len(seg) > meter + 2:
            bars.append(seg[:meter])
            seg = seg[meter:]
        if seg:
            bars.append(seg)
    return bars, meter


def tempo_info(beats: np.ndarray, bars: list[list[float]], meter: int) -> dict:
    if len(beats) < 3:
        return {"bpm": 0.0, "stability": 0.0, "curve": [], "meter": meter}
    ibi = np.diff(beats)
    ibi = ibi[(ibi > 0.2) & (ibi < 2.0)]
    med = float(np.median(ibi))
    # доли beat_this стоят на кадрах по 20 мс: по одной доле разброс не виден — меряем отрезками в два такта
    k = max(2, 2 * meter)
    if len(beats) > k + 4:
        span = (beats[k:] - beats[:-k]) / k
        span = span[(span > 0.2) & (span < 2.0)]
        mad = float(np.median(np.abs(span - np.median(span)))) * 1.4826
    else:
        mad = float(np.median(np.abs(ibi - med))) * 1.4826
    curve = []
    for bar in bars:
        if len(bar) >= 2:
            curve.append([round(bar[0], 2), round(60.0 / float(np.mean(np.diff(bar))), 1)])
    first = np.median([c[1] for c in curve[: max(1, len(curve) // 6)]]) if curve else 60 / med
    last = np.median([c[1] for c in curve[-max(1, len(curve) // 6):]]) if curve else 60 / med
    lens = Counter(len(b) for b in bars if len(b) >= 2)
    share = lens[meter] / max(1, sum(lens.values()))
    return {"bpm": round(60.0 / med, 1), "stability": round(mad / med, 4), "curve": curve, "meter": meter,
            "meter_share": round(share, 2), "meter_sure": bool(meter in (2, 3, 4, 6) and share >= 0.5),
            "start_bpm": round(float(first), 1), "end_bpm": round(float(last), 1)}


# ---------- ударные: события и грув ----------

def drums_of(d: np.ndarray, sr: int, bars: list[list[float]], beats: np.ndarray) -> dict:
    import librosa
    hop, nfft = sr // 100, 1024
    S = np.abs(librosa.stft(d, n_fft=nfft, hop_length=hop)) ** 2
    f = librosa.fft_frequencies(sr=sr, n_fft=nfft)

    def band(lo, hi):
        e = S[(f >= lo) & (f < hi)].sum(axis=0)
        return np.log10(e + 1e-10)

    low, mid, noise, high = band(30, 130), band(150, 900), band(1800, 5000), band(7000, 16000)

    def flux(x):
        fl = np.zeros_like(x)
        fl[2:] = np.maximum(0, x[2:] - np.maximum(x[1:-1], x[:-2]))
        return fl

    fl_low, fl_mid, fl_noise, fl_high = flux(low), flux(mid), flux(noise), flux(high)
    gate = {k: np.percentile(v, 95) - 3.0 for k, v in (("low", low), ("mid", mid), ("noise", noise), ("high", high))}

    def peaks(fl, lvl, thr, floor):
        from scipy.ndimage import median_filter
        base = median_filter(fl, size=151, mode="nearest")
        out = []
        last = -10
        for i in range(3, len(fl) - 3):
            if fl[i] < thr + base[i] or lvl[i] < floor or fl[i] < fl[i - 3:i + 4].max():
                continue
            if i - last < 5:      # 50 мс: двойной пик одного удара
                continue
            out.append(i)
            last = i
        if len(out) > 20:
            strong = np.percentile(fl[out], 90)
            out = [i for i in out if fl[i] >= 0.33 * strong]
        return out

    # окно по центру кадра видит удар на четверть окна раньше, чем он звучит
    lead = nfft / 4 / sr
    hits: list[tuple[float, str]] = []
    for i in peaks(fl_low, low, 0.35, gate["low"]):
        # бочка — удар, где низ растёт сильнее середины (иначе это низ малого или тома)
        if fl_low[i] >= 0.6 * fl_mid[max(0, i - 1):i + 2].max():
            hits.append((i / 100.0 + lead, "K"))
    for i in peaks(fl_mid, mid, 0.3, gate["mid"]):
        # малый: тело 150–900 Гц и шум пружин 2–5 кГц одновременно
        if fl_noise[max(0, i - 2):i + 3].max() > 0.2 and fl_mid[i] > 0.7 * fl_low[max(0, i - 1):i + 2].max():
            hits.append((i / 100.0 + lead, "S"))
    for i in peaks(fl_high, high, 0.25, gate["high"]):
        hits.append((i / 100.0 + lead, "H"))
    hits.sort()
    sub = subdivision(hits, beats)

    # сетка: доля на 4 шестнадцатых (или на 3 — триольная пульсация), удар → ближайший шаг, отклонение в мс — грув
    bar_rows = []
    offsets: dict[str, list[float]] = {"K": [], "S": [], "H": []}
    snare_back: list[float] = []
    kick_one: list[float] = []
    phases8: list[float] = []
    phases16: list[float] = []
    all_beats = np.asarray(beats)
    for bi, bar in enumerate(bars):
        nb = len(bar)
        nxt_i = int(np.searchsorted(all_beats, bar[-1])) + 1
        end = all_beats[nxt_i] if nxt_i < len(all_beats) else bar[-1] + (np.median(np.diff(bar)) if nb > 1 else 0.5)
        pts = list(bar) + [float(end)]
        steps = [pts[k] + (pts[k + 1] - pts[k]) * s / sub for k in range(nb) for s in range(sub)]
        row = {"bar": bi, "t": round(bar[0], 3), "steps": nb * sub, "K": ["."] * (nb * sub), "S": ["."] * (nb * sub), "H": ["."] * (nb * sub)}
        for t, kind in hits:
            if not pts[0] - 0.06 <= t < pts[-1] - 0.06:
                continue
            k = int(np.argmin([abs(t - s) for s in steps]))
            beat_len = pts[k // sub + 1] - pts[k // sub]
            if abs(t - steps[k]) > beat_len / (2 * sub) + 0.01:
                continue
            row[kind][k] = "x"
            off = (t - steps[k]) * 1000
            offsets[kind].append(off)
            beat_k = k // sub
            ph = (t - pts[beat_k]) / beat_len if beat_len > 0 else 0
            if sub == 4 and kind in ("H", "S"):
                if 0.42 <= ph <= 0.74:
                    phases8.append(ph)
                elif 0.17 <= ph <= 0.36:
                    phases16.append(ph)
            if kind == "S" and k % sub == 0 and nb >= 4 and beat_k in (1, 3):
                snare_back.append(off)
            if kind == "K" and k == 0:
                kick_one.append(off)
        for kind in "KSH":
            row[kind] = "".join(row[kind])
        bar_rows.append(row)

    def swing(ph, straight, full):
        if len(ph) < 12:
            return None
        return round(float((np.median(ph) - straight) / (full - straight) * 100), 1)

    allo = np.array(offsets["K"] + offsets["S"] + offsets["H"])
    return {
        "hits": [[round(t, 3), k] for t, k in hits],
        "bars": bar_rows,
        "counts": {k: sum(1 for _, x in hits if x == k) for k in "KSH"},
        "subdivision": sub,
        "swing8": swing(phases8, 0.5, 2 / 3) if sub == 4 else None,
        "swing16": swing(phases16, 0.25, 1 / 3) if sub == 4 else None,
        "snare_backbeat_ms": round(float(np.mean(snare_back)), 1) if len(snare_back) >= 6 else None,
        "kick_one_ms": round(float(np.mean(kick_one)), 1) if len(kick_one) >= 6 else None,
        "tightness_ms": round(float(np.std(allo)), 1) if allo.size >= 16 else None,
    }


def align_beats(beats: np.ndarray, drum: np.ndarray, sr: int) -> tuple[np.ndarray, float]:
    """Доли beat_this стоят на кадрах по 20 мс и опаздывают/спешат против ударов: сдвиг — медиана отклонения
    бочки и малого от ближайшей доли (грув меряется относительно бочки, как его слышит музыкант)."""
    if len(beats) < 16:
        return beats, 0.0
    import librosa
    env = librosa.onset.onset_strength(y=C.resample(drum, sr, 22050), sr=22050, hop_length=110, fmax=5000)
    fps = 22050 / 110
    offs = []
    for b in beats:
        i0, i1 = int((b - 0.06) * fps), int((b + 0.06) * fps)
        if i0 < 0 or i1 >= len(env):
            continue
        w = env[i0:i1]
        k = int(np.argmax(w))
        if w[k] > np.percentile(env, 80):
            offs.append((i0 + k) / fps - b)
    if len(offs) < 16:
        return beats, 0.0
    shift = float(np.median(offs))
    return (beats + shift if abs(shift) > 0.004 else beats), shift


def subdivision(hits: list[tuple[float, str]], beats: np.ndarray) -> int:
    """Доля дробится ровно (восьмые/шестнадцатые) или на три (триоли, 6/8, шаффл): гистограмма положения ударов в доле."""
    if len(beats) < 8:
        return 4
    ph = []
    for t, kind in hits:
        if kind == "K":
            continue
        i = int(np.searchsorted(beats, t)) - 1
        if 0 <= i < len(beats) - 1:
            ph.append((t - beats[i]) / (beats[i + 1] - beats[i]))
    arr = np.asarray(ph)
    if arr.size < 30:
        return 4

    def near(c: float) -> int:
        return int(np.sum(np.abs(arr - c) < 0.045))

    binary = near(0.25) + near(0.5) + near(0.75)
    ternary = near(1 / 3) + near(2 / 3)
    return 3 if ternary > 1.3 * binary else 4


def common_pattern(rows: list[dict], a: int, b: int) -> dict | None:
    """Типичный такт части: на каждом шаге удар ставится, если он там в 45%+ тактов с тем же числом шагов;
    share — насколько такты части совпадают с этим рисунком (1 — все одинаковые)."""
    part = [r for r in rows[a:b] if r["steps"] >= 8 and ("x" in r["K"] + r["S"] + r["H"])]
    if not part:
        return None
    n = Counter(r["steps"] for r in part).most_common(1)[0][0]
    part = [r for r in part if r["steps"] == n]
    pat = {}
    for kind in "KSH":
        votes = np.array([[c == "x" for c in r[kind]] for r in part], dtype=np.float32).mean(axis=0)
        pat[kind] = "".join("x" if v >= 0.45 else "." for v in votes)
    agree = np.mean([np.mean([r[k][i] == pat[k][i] for k in "KSH" for i in range(n)]) for r in part])
    return {"K": pat["K"], "S": pat["S"], "H": pat["H"], "share": round(float(agree), 2)}


# ---------- голос: тон и ноты ----------

def vocal_f0(v: np.ndarray, sr: int) -> np.ndarray:
    """Тон голоса по 10 мс (RMVPE из Seed-VC, веса — общий кэш HF). Кусками по 30 с — длинная песня не съест память."""
    import torch
    from huggingface_hub import hf_hub_download
    global _rmvpe
    if str(C.SEED) not in sys.path:
        sys.path.append(str(C.SEED))
    from modules.rmvpe import RMVPE
    dev = C.device(1.0)
    if _rmvpe is None or not str(_rmvpe.device).startswith(dev):
        _rmvpe = RMVPE(hf_hub_download("lj1995/VoiceConversionWebUI", "rmvpe.pt"), is_half=False, device=dev)
    y = C.resample(v, sr, 16000)
    n_frames = len(y) // 160 + 1
    out = np.zeros(n_frames, dtype=np.float32)
    step = 16000 * 30
    for s in range(0, len(y), step):
        chunk = y[s:s + step]
        if len(chunk) < 1600:
            break
        with torch.no_grad():
            f = _rmvpe.infer_from_audio(torch.from_numpy(np.ascontiguousarray(chunk)), thred=0.03)
        a = s // 160
        n = min(len(f), n_frames - a)
        out[a:a + n] = f[:n]
    # тон там, где голос реально звучит (в стеме бывает протечка инструментов)
    rms = frame_rms(v, sr, 100)
    n = min(len(out), len(rms))
    loud = rms[:n] > max(1e-4, np.percentile(rms[rms > 0], 90) * 0.06) if np.any(rms > 0) else np.zeros(n, bool)
    out[:n][~loud] = 0
    return out


def frame_rms(x: np.ndarray, sr: int, fps: int) -> np.ndarray:
    hop = sr // fps
    n = len(x) // hop
    if n == 0:
        return np.zeros(0)
    return np.sqrt(np.mean(x[: n * hop].reshape(n, hop) ** 2, axis=1) + 1e-12)


def onset_env(x: np.ndarray, sr: int, fps: int = 100) -> np.ndarray:
    """Сила атаки (спектральный поток) по кадрам 10 мс — начала слогов на одной высоте."""
    import librosa
    hop = sr // fps
    return librosa.onset.onset_strength(y=x, sr=sr, hop_length=hop, n_mels=64, fmax=8000)


def notes_from_f0(f0: np.ndarray, onset: np.ndarray | None = None, fps: int = 100) -> list[list[float]]:
    """Ноты из тона: новая нота — на паузе, на скачке больше 0,8 полутона (держится 30 мс) или на атаке слога."""
    from scipy.ndimage import median_filter
    m = C.hz_to_midi(f0.astype(np.float64))
    voiced = m > 0
    ms = m.copy()
    ms[voiced] = median_filter(m, size=5)[voiced]
    notes: list[list[float]] = []
    n = len(ms)
    on_thr = None
    if onset is not None and len(onset):
        on_thr = np.percentile(onset, 85)
    i = 0
    while i < n:
        if not voiced[i]:
            i += 1
            continue
        j = i
        cur = [ms[i]]
        while j + 1 < n and voiced[j + 1]:
            nxt = ms[j + 1]
            ref = float(np.median(cur[-15:]))
            jump = abs(nxt - ref) > 0.8 and j + 3 < n and all(voiced[j + 1:j + 4]) and all(abs(ms[j + 1:j + 4] - ref) > 0.8)
            attack = on_thr is not None and j + 1 < len(onset) and onset[j + 1] > on_thr and len(cur) >= 8 \
                and onset[j + 1] >= onset[max(0, j - 2):j + 4].max()
            if jump or attack:
                break
            j += 1
            cur.append(nxt)
        if (j - i + 1) >= 6:     # короче 60 мс — не нота, а переход
            notes.append([round(i / fps, 3), round((j + 1) / fps, 3), round(float(np.median(cur)), 2)])
        i = j + 1
    # склейка: одна высота, пауза меньше 30 мс и без атаки — это одна нота с провалом тона
    merged: list[list[float]] = []
    for nt in notes:
        if merged and nt[0] - merged[-1][1] < 0.03 and abs(nt[2] - merged[-1][2]) < 0.5:
            k = int(nt[0] * fps)
            if on_thr is None or k >= len(onset) or onset[k] < on_thr:
                merged[-1][1] = nt[1]
                continue
        merged.append(nt)
    return merged


def poly_notes(wav_path: Path, lo: float | None, hi: float | None, onset: float = 0.5, frame: float = 0.3) -> list[list[float]]:
    """Basic Pitch (ONNX, процессор): полифонические ноты [начало, конец, midi, сила]."""
    import basic_pitch
    from basic_pitch.inference import Model, predict
    global _bp
    if _bp is None:
        _bp = Model(Path(basic_pitch.__file__).parent / "saved_models" / "icassp_2022" / "nmp.onnx")
    _, _, events = predict(str(wav_path), _bp, onset_threshold=onset, frame_threshold=frame, minimum_note_length=90,
                           minimum_frequency=lo, maximum_frequency=hi, multiple_pitch_bends=False, melodia_trick=True)
    return sorted([[round(float(s), 3), round(float(e), 3), int(p), round(float(a), 2)] for s, e, p, a, _ in events])


def skyline(notes: list[list[float]], lowest: bool = False) -> list[list[float]]:
    """Одноголосие из полифонии: в каждый момент — верхняя (или нижняя, для баса) нота."""
    if not notes:
        return []
    ev = sorted(notes, key=lambda n: (n[0], -n[2] if not lowest else n[2]))
    out: list[list[float]] = []
    for n in ev:
        if out and n[0] < out[-1][1] - 0.03:
            better = n[2] > out[-1][2] if not lowest else n[2] < out[-1][2]
            if not better:
                continue
            if n[0] - out[-1][0] < 0.05:
                out[-1] = list(n)
                continue
            out[-1][1] = n[0]
        out.append(list(n))
    return [n for n in out if n[1] - n[0] >= 0.06]


# ---------- гармония: хрома, тональность, аккорды ----------

MAJOR = np.array([6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88])
MINOR = np.array([6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17])
KEY_RU = {"major": "мажор", "minor": "минор"}
QUAL = {  # интервалы от корня и вес ступени в шаблоне
    "": [(0, 1.0), (4, 0.85), (7, 0.8)],
    "m": [(0, 1.0), (3, 0.85), (7, 0.8)],
    "7": [(0, 1.0), (4, 0.8), (7, 0.7), (10, 0.65)],
    "maj7": [(0, 1.0), (4, 0.8), (7, 0.7), (11, 0.65)],
    "m7": [(0, 1.0), (3, 0.8), (7, 0.7), (10, 0.65)],
    "dim": [(0, 1.0), (3, 0.85), (6, 0.8)],
    "sus4": [(0, 1.0), (5, 0.85), (7, 0.8)],
    "5": [(0, 1.0), (7, 0.9)],               # квинта без терции — «пауэр-аккорд» перегруженной гитары
}


def beat_chroma(y: np.ndarray, sr: int, beats: np.ndarray, duration: float, bass: bool = False) -> np.ndarray:
    """Хрома по долям (медиана кадров между долями): [12, число долей]."""
    import librosa
    y22 = C.resample(y, sr, 22050)
    hop = 512
    if bass:
        ch = librosa.feature.chroma_cqt(y=y22, sr=22050, hop_length=hop, fmin=librosa.note_to_hz("C1"), n_octaves=4, bins_per_octave=36)
    else:
        ch = librosa.feature.chroma_cqt(y=y22, sr=22050, hop_length=hop, fmin=librosa.note_to_hz("C2"), n_octaves=6, bins_per_octave=36)
    energy = librosa.feature.rms(y=y22, hop_length=hop)[0]
    n = min(ch.shape[1], len(energy))          # CQT и RMS иногда расходятся на кадр
    ch = ch[:, :n] * (energy[None, :n] / (energy.max() + 1e-9)) ** 0.5
    frames = librosa.time_to_frames(np.concatenate([beats, [duration]]), sr=22050, hop_length=hop)
    frames = np.clip(frames, 0, ch.shape[1] - 1)
    out = np.zeros((12, len(beats)))
    for i in range(len(beats)):
        a, b = frames[i], max(frames[i] + 1, frames[i + 1])
        out[:, i] = np.median(ch[:, a:b], axis=1)
    return out


def key_of(ch: np.ndarray) -> dict:
    prof = ch.sum(axis=1)
    if prof.sum() <= 0:
        return {"tonic": "C", "mode": "major", "name": "не определить", "confidence": 0.0, "pc": 0}
    scores = []
    for mode, p in (("major", MAJOR), ("minor", MINOR)):
        for r in range(12):
            scores.append((float(np.corrcoef(prof, np.roll(p, r))[0, 1]), r, mode))
    all_r = {f"{m}{rt}": round(rr, 4) for rr, rt, m in scores}
    scores.sort(reverse=True)
    r, root, mode = scores[0]
    return {"tonic": C.NOTE_NAMES[root], "mode": mode, "pc": root, "name": f"{C.NOTE_NAMES[root]} {KEY_RU[mode]}",
            "confidence": round(r, 3), "margin": round(r - scores[1][0], 3),
            "runner_up": f"{C.NOTE_NAMES[scores[1][1]]} {KEY_RU[scores[1][2]]}", "all": all_r}


def chord_templates() -> tuple[list[str], np.ndarray, list[tuple[int, str]]]:
    names, temps, meta = [], [], []
    for q, iv in QUAL.items():
        for r in range(12):
            t = np.zeros(12)
            for k, w in iv:
                t[(r + k) % 12] = w
            names.append(C.NOTE_NAMES[r] + q)
            temps.append(t / np.linalg.norm(t))
            meta.append((r, q))
    return names, np.array(temps), meta


def chords_of(ch: np.ndarray, bch: np.ndarray, beats: np.ndarray, beat_pos: np.ndarray, meter: int, key: dict, duration: float) -> list[list]:
    """Аккорд на каждую долю: шаблон + бас на корне + лёгкий довод «своей» тональности; Витерби — смена дёшево на
    сильной доле и в середине такта, дорого на слабых (аккорд редко меняется на каждую долю)."""
    names, T, meta = chord_templates()
    B = ch.shape[1]
    if B == 0:
        return []
    norm = np.linalg.norm(ch, axis=0) + 1e-9
    cn = ch / norm
    energy = norm / (np.percentile(norm, 95) + 1e-9)
    bn = bch / (np.linalg.norm(bch, axis=0) + 1e-9)
    sim = T @ cn                                            # [аккорды, доли]
    diat = set((key.get("pc", 0) + k) % 12 for k in ([0, 2, 4, 5, 7, 9, 11] if key.get("mode") == "major" else [0, 2, 3, 5, 7, 8, 10]))
    for ci, (r, q) in enumerate(meta):
        sim[ci] += 0.12 * bn[r]                              # бас на корне — сильный довод
        tones = {(r + k) % 12 for k, _ in QUAL[q]}
        sim[ci] += 0.03 * (1 if tones <= diat else 0)
        if q not in ("", "m", "5"):
            sim[ci] -= 0.06                                  # септаккорды и sus — только если явно лучше трезвучия
        elif q == "5":
            sim[ci] -= 0.02                                  # квинта — если терции правда нет
    n_state = len(names) + 1                                 # последний — «нет аккорда»
    emis = np.vstack([sim, np.full((1, B), 0.35)])
    emis[-1, energy < 0.08] = 2.0
    kappa = 14.0
    logE = kappa * emis
    stay, change_db, change_off = np.log(0.92), np.log(0.08 / (n_state - 1)), np.log(0.015 / (n_state - 1))
    dp = logE[:, 0].copy()
    back = np.zeros((n_state, B), dtype=np.int32)
    for b in range(1, B):
        strong = beat_pos[b] == 0 or (meter % 2 == 0 and beat_pos[b] == meter // 2)
        ch_cost = change_db if strong else change_off
        best_prev = int(np.argmax(dp))
        cand_change = dp[best_prev] + ch_cost
        cand_stay = dp + stay
        take_change = cand_change > cand_stay
        back[:, b] = np.where(take_change, best_prev, np.arange(n_state))
        dp = np.where(take_change, cand_change, cand_stay) + logE[:, b]
    path = np.zeros(B, dtype=np.int32)
    path[-1] = int(np.argmax(dp))
    for b in range(B - 1, 0, -1):
        path[b - 1] = back[path[b], b]
    out: list[list] = []
    ends = np.concatenate([beats[1:], [duration]])
    for b in range(B):
        s = path[b]
        name = "N" if s == len(names) else names[s]
        if name != "N":
            r = meta[s][0]
            bass_pc = int(np.argmax(bch[:, b])) if bch[:, b].max() > 0 else r
            tones = {(r + k) % 12 for k, _ in QUAL[meta[s][1]]}
            if bass_pc != r and bass_pc in tones and bn[bass_pc, b] > 0.8:
                name = f"{name}/{C.NOTE_NAMES[bass_pc]}"
        if out and out[-1][2] == name:
            out[-1][1] = round(float(ends[b]), 3)
        else:
            out.append([round(float(beats[b]), 3), round(float(ends[b]), 3), name])
    # слэш-аккорды на одну долю — чаще проходящий бас, чем смена аккорда
    clean: list[list] = []
    for seg in out:
        base = seg[2].split("/")[0]
        if clean and clean[-1][2].split("/")[0] == base:
            clean[-1][1] = seg[1]
            continue
        clean.append(seg)
    return clean


# ---------- форма ----------

def sections_of(mix_m: np.ndarray, sr: int, ch: np.ndarray, beats: np.ndarray, bars: list[list[float]], vocal_act: np.ndarray,
                duration: float, sung: bool, words: list[list] | None = None) -> list[dict]:
    """Спектральная кластеризация самоподобия по долям (McFee & Ellis 2014), границы — на сильных долях."""
    import librosa
    import scipy.linalg
    import scipy.ndimage
    import scipy.sparse.csgraph
    from sklearn.cluster import KMeans
    B = len(beats)
    if B < 16 or not bars:
        return [{"t0": 0.0, "t1": round(duration, 2), "label": "A", "name": "целиком", "vocals": sung, "energy": 0.0}]
    y22 = C.resample(mix_m, sr, 22050)
    mf = librosa.feature.mfcc(y=y22, sr=22050, n_mfcc=20, hop_length=512)
    fr = np.clip(librosa.time_to_frames(np.concatenate([beats, [duration]]), sr=22050, hop_length=512), 0, mf.shape[1] - 1)
    M = np.stack([mf[:, fr[i]:max(fr[i] + 1, fr[i + 1])].mean(axis=1) for i in range(B)], axis=1)
    Cn = librosa.util.normalize(ch + 1e-9, norm=2, axis=0)
    R = librosa.segment.recurrence_matrix(Cn, width=3, mode="affinity", sym=True)
    Rf = librosa.segment.timelag_filter(scipy.ndimage.median_filter)(R, size=(1, 7))
    dist = np.sum(np.diff(M, axis=1) ** 2, axis=0)
    sigma = np.median(dist) + 1e-9
    ps = np.exp(-dist / sigma)
    Rp = np.diag(ps, k=1) + np.diag(ps, k=-1)
    dp, dr = Rp.sum(axis=1), Rf.sum(axis=1)
    mu = dp.dot(dp + dr) / (np.sum((dp + dr) ** 2) + 1e-9)
    A = mu * Rf + (1 - mu) * Rp
    L = scipy.sparse.csgraph.laplacian(A, normed=True)
    _, evecs = scipy.linalg.eigh(L)
    evecs = scipy.ndimage.median_filter(evecs, size=(9, 1))
    k = 3 if duration < 70 else 5 if duration < 160 else 6 if duration < 320 else 7
    k = min(k, B // 8)
    Cm = np.cumsum(evecs ** 2, axis=1) ** 0.5
    X = evecs[:, :k] / (Cm[:, k - 1:k] + 1e-9)
    lab = KMeans(n_clusters=k, n_init=10, random_state=0).fit_predict(X)
    # доли → такты (метка такта — большинство его долей), короткие куски (< 2 тактов) — к соседу
    bar_start = [int(np.argmin(np.abs(beats - b[0]))) for b in bars]
    bar_lab = []
    for i, s in enumerate(bar_start):
        e = bar_start[i + 1] if i + 1 < len(bar_start) else B
        bar_lab.append(Counter(lab[s:max(s + 1, e)]).most_common(1)[0][0])
    segs: list[list[int]] = []          # [первый такт, последний+1, метка]
    for i, l in enumerate(bar_lab):
        if segs and segs[-1][2] == l:
            segs[-1][1] = i + 1
        else:
            segs.append([i, i + 1, l])
    def seg_sec(sg: list[int]) -> float:
        t0 = bars[sg[0]][0]
        t1 = bars[sg[1]][0] if sg[1] < len(bars) else duration
        return t1 - t0

    changed = True
    while changed and len(segs) > 1:
        changed = False
        for j, s in enumerate(segs):
            if s[1] - s[0] < 2 or seg_sec(s) < 6.0:
                if j == 0:
                    segs[1][0] = s[0]
                elif j == len(segs) - 1 or (segs[j - 1][1] - segs[j - 1][0]) >= (segs[j + 1][1] - segs[j + 1][0]):
                    segs[j - 1][1] = s[1]
                else:
                    segs[j + 1][0] = s[0]
                del segs[j]
                changed = True
                break
        for j in range(len(segs) - 1, 0, -1):
            if segs[j][2] == segs[j - 1][2]:
                segs[j - 1][1] = segs[j][1]
                del segs[j]
                changed = True
    rms = frame_rms(mix_m, sr, 10)
    out = []
    for a, b, l in segs:
        t0 = bars[a][0] if a > 0 else 0.0
        t1 = bars[b][0] if b < len(bars) else duration
        va = vocal_act[int(t0 * 10):max(int(t0 * 10) + 1, int(t1 * 10))]
        en = rms[int(t0 * 10):max(int(t0 * 10) + 1, int(t1 * 10))]
        out.append({"t0": round(float(t0), 2), "t1": round(float(t1), 2), "bars": [a, b], "label": "ABCDEFGH"[int(l) % 8],
                    "vocals": bool(va.size and va.mean() > 0.3), "energy": round(float(20 * np.log10(np.mean(en) + 1e-9)), 1)})
    # метки по порядку появления: первая часть — A, следующая новая — B…
    order: dict[str, str] = {}
    for s in out:
        if s["label"] not in order:
            order[s["label"]] = "ABCDEFGH"[len(order)]
        s["label"] = order[s["label"]]
    name_sections(out, sung, words)
    return out


def name_sections(secs: list[dict], sung: bool, words: list[list] | None = None) -> None:
    if not sung:
        seen: Counter = Counter()
        loud = np.median([s["energy"] for s in secs]) if secs else 0
        for s in secs:
            seen[s["label"]] += 1
            prime = "′" * (seen[s["label"]] - 1)
            dyn = "громко" if s["energy"] > loud + 3 else "тихо" if s["energy"] < loud - 3 else ""
            s["name"] = f"часть {s['label']}{prime}" + (f" ({dyn})" if dyn else "")
        return
    voc = [i for i, s in enumerate(secs) if s["vocals"]]
    if not voc:
        for s in secs:
            s["name"] = "проигрыш"
        return
    words = words or []

    def text_of(s: dict) -> set[str]:
        return {w[2].lower().strip(".,!?\"'…") for w in words if s["t0"] <= w[0] < s["t1"] and len(w[2]) > 2}

    first, last = voc[0], voc[-1]
    by: dict[str, list[int]] = {}
    for i in voc:
        by.setdefault(secs[i]["label"], []).append(i)
    rep_ = {l: ix for l, ix in by.items() if len(ix) >= 2}

    def lyric_repeat(ix: list[int]) -> float:
        sets = [text_of(secs[i]) for i in ix]
        sets = [x for x in sets if x]
        if len(sets) < 2:
            return 0.0
        sims = [len(a & b) / max(1, len(a | b)) for k, a in enumerate(sets) for b in sets[k + 1:]]
        return float(np.mean(sims))

    energy_med = float(np.median([secs[i]["energy"] for i in voc]))
    score = {l: (np.mean([secs[i]["energy"] for i in ix]) - energy_med) / 3 + 2.5 * lyric_repeat(ix) + 0.2 * len(ix)
             for l, ix in rep_.items()}
    chorus = max(score, key=score.get) if score else None
    verse = None
    rest = {l: ix for l, ix in rep_.items() if l != chorus}
    if rest:
        verse = min(rest, key=lambda l: (lyric_repeat(rest[l]), -len(rest[l])))
    num: Counter = Counter()
    for i, s in enumerate(secs):
        if i < first:
            base = "вступление"
        elif i > last:
            base = "концовка"
        elif not s["vocals"]:
            base = "проигрыш"
        elif s["label"] == chorus:
            base = "припев"
        elif s["label"] == verse:
            base = "куплет"
        elif i + 1 < len(secs) and secs[i + 1]["label"] == chorus:
            base = "предприпев"
        elif s["label"] in rep_:
            base = "куплет"
        else:
            base = "бридж"
        num[base] += 1
        s["name"] = f"{base} {num[base]}" if base in ("куплет", "припев") else base


# ---------- инструменты, жанр, настроение (AST AudioSet) ----------

RU = {
    "Guitar": "гитара", "Electric guitar": "электрогитара", "Bass guitar": "бас-гитара", "Acoustic guitar": "акустическая гитара",
    "Steel guitar, slide guitar": "слайд-гитара", "Tapping (guitar technique)": "гитара тэппингом", "Strum": "гитара боем",
    "Banjo": "банджо", "Sitar": "ситар", "Mandolin": "мандолина", "Zither": "цитра", "Ukulele": "укулеле",
    "Keyboard (musical)": "клавишные", "Piano": "фортепиано", "Electric piano": "электропиано", "Organ": "орган",
    "Electronic organ": "электроорган", "Hammond organ": "орган Хаммонда", "Synthesizer": "синтезатор", "Sampler": "сэмплер",
    "Harpsichord": "клавесин", "Drum kit": "ударная установка", "Drum machine": "драм-машина", "Snare drum": "малый барабан",
    "Bass drum": "бас-барабан", "Timpani": "литавры", "Tabla": "табла", "Cymbal": "тарелки", "Hi-hat": "хай-хэт",
    "Tambourine": "бубен", "Maraca": "маракасы", "Gong": "гонг", "Tubular bells": "колокола-трубки",
    "Marimba, xylophone": "маримба/ксилофон", "Glockenspiel": "металлофон", "Vibraphone": "вибрафон", "Steelpan": "стил-пэн",
    "Orchestra": "оркестр", "Brass instrument": "медные духовые", "French horn": "валторна", "Trumpet": "труба",
    "Trombone": "тромбон", "Bowed string instrument": "смычковые", "String section": "струнная группа",
    "Violin, fiddle": "скрипка", "Pizzicato": "пиццикато", "Cello": "виолончель", "Double bass": "контрабас",
    "Wind instrument, woodwind instrument": "деревянные духовые", "Flute": "флейта", "Saxophone": "саксофон",
    "Clarinet": "кларнет", "Harp": "арфа", "Bell": "колокольчик", "Church bell": "церковный колокол", "Harmonica": "губная гармошка",
    "Accordion": "аккордеон", "Bagpipes": "волынка", "Didgeridoo": "диджериду", "Theremin": "терменвокс",
    "Scratching (performance technique)": "скретч", "Choir": "хор",
    # голос
    "Singing": "пение", "Male singing": "мужской вокал", "Female singing": "женский вокал", "Child singing": "детский голос",
    "Synthetic singing": "синтетический голос (вокодер/автотюн)", "Rapping": "рэп", "Humming": "напев без слов",
    "Yodeling": "йодль", "Chant": "речитатив-распев", "Speech": "речь", "Whispering": "шёпот", "Screaming": "крик",
    # жанры
    "Pop music": "поп", "Hip hop music": "хип-хоп", "Rock music": "рок", "Heavy metal": "хеви-метал", "Punk rock": "панк-рок",
    "Grunge": "гранж", "Progressive rock": "прог-рок", "Rock and roll": "рок-н-ролл", "Psychedelic rock": "психоделический рок",
    "Rhythm and blues": "ритм-н-блюз", "Soul music": "соул", "Reggae": "регги", "Country": "кантри", "Swing music": "свинг",
    "Bluegrass": "блюграсс", "Funk": "фанк", "Folk music": "фолк", "Middle Eastern music": "ближневосточная музыка",
    "Jazz": "джаз", "Disco": "диско", "Classical music": "классическая музыка", "Opera": "опера",
    "Electronic music": "электроника", "House music": "хаус", "Techno": "техно", "Dubstep": "дабстеп",
    "Drum and bass": "драм-н-бейс", "Electronica": "электроника (эмбиентная)", "Electronic dance music": "EDM",
    "Ambient music": "эмбиент", "Trance music": "транс", "Music of Latin America": "латина", "Salsa music": "сальса",
    "Flamenco": "фламенко", "Blues": "блюз", "Music for children": "детская музыка", "New-age music": "нью-эйдж",
    "Vocal music": "вокальная музыка", "A capella": "а капелла", "Music of Africa": "африканская музыка", "Afrobeat": "афробит",
    "Christian music": "христианская музыка", "Gospel music": "госпел", "Music of Asia": "азиатская музыка",
    "Music of Bollywood": "болливуд", "Ska": "ска", "Traditional music": "народная музыка", "Independent music": "инди",
    "Soundtrack music": "саундтрек", "Film score": "киномузыка", "Video game music": "музыка из игр",
    "Lullaby": "колыбельная", "Wedding music": "свадебная музыка", "Christmas music": "рождественская музыка",
    "Dance music": "танцевальная", "Theme music": "музыкальная тема", "Jingle (music)": "джингл", "Background music": "фоновая музыка",
    # настроение
    "Happy music": "весёлая", "Funny music": "смешная", "Sad music": "грустная", "Tender music": "нежная",
    "Exciting music": "заводная", "Angry music": "злая", "Scary music": "жуткая",
}
INSTR = [
    "Guitar", "Electric guitar", "Bass guitar", "Acoustic guitar", "Steel guitar, slide guitar", "Tapping (guitar technique)",
    "Strum", "Banjo", "Sitar", "Mandolin", "Zither", "Ukulele", "Keyboard (musical)", "Piano", "Electric piano", "Organ",
    "Electronic organ", "Hammond organ", "Synthesizer", "Sampler", "Harpsichord", "Drum kit", "Drum machine", "Snare drum",
    "Bass drum", "Timpani", "Tabla", "Cymbal", "Hi-hat", "Tambourine", "Maraca", "Gong", "Tubular bells", "Marimba, xylophone",
    "Glockenspiel", "Vibraphone", "Steelpan", "Orchestra", "Brass instrument", "French horn", "Trumpet", "Trombone",
    "Bowed string instrument", "String section", "Violin, fiddle", "Pizzicato", "Cello", "Double bass",
    "Wind instrument, woodwind instrument", "Flute", "Saxophone", "Clarinet", "Harp", "Bell", "Church bell", "Harmonica",
    "Accordion", "Bagpipes", "Didgeridoo", "Theremin", "Scratching (performance technique)",
]
VOICE = ["Singing", "Male singing", "Female singing", "Child singing", "Synthetic singing", "Rapping", "Humming", "Yodeling",
         "Chant", "Choir", "Whispering", "Screaming"]
MOOD = ["Happy music", "Funny music", "Sad music", "Tender music", "Exciting music", "Angry music", "Scary music"]
GENRE = [k for k in RU if k not in INSTR and k not in VOICE and k not in MOOD and k != "Speech"]
# в каких стемах честно искать класс (в остальных — протечка): микс слушается всегда
DRUM_CLASSES = {"Drum kit", "Drum machine", "Snare drum", "Bass drum", "Timpani", "Tabla", "Cymbal", "Hi-hat", "Tambourine",
                "Maraca", "Gong", "Scratching (performance technique)"}
BASS_CLASSES = {"Bass guitar", "Double bass", "Synthesizer", "Organ", "Cello"}


def stems_for(label: str) -> list[str]:
    if label in DRUM_CLASSES:
        return ["drums"]
    if label in VOICE:
        return ["vocals"]
    if label in BASS_CLASSES:
        return ["bass", "other"]
    return ["other"]


def ast_scores(windows: list[np.ndarray]) -> np.ndarray:
    """Вероятности 527 классов AudioSet для окон по 10 с (16 кГц)."""
    import torch
    from transformers import ASTFeatureExtractor, ASTForAudioClassification
    global _ast
    dev = C.device(1.0)
    if _ast is None:
        fe = ASTFeatureExtractor.from_pretrained(str(C.AST_DIR))
        model = ASTForAudioClassification.from_pretrained(str(C.AST_DIR)).eval()
        _ast = (fe, model)
    fe, model = _ast
    model.to(dev)
    out = []
    for i in range(0, len(windows), 8):
        batch = windows[i:i + 8]
        feats = fe(batch, sampling_rate=16000, return_tensors="pt")["input_values"].to(dev)
        with torch.no_grad():
            out.append(torch.sigmoid(model(feats).logits).cpu().numpy())
    model.cpu()
    return np.concatenate(out) if out else np.zeros((0, model.config.num_labels))


def labels_of(stems: dict[str, np.ndarray], mix_m: np.ndarray, sr: int, duration: float) -> dict:
    step = 10.0
    starts = np.arange(0, max(duration - 3.0, 0.1), step)

    def windows(x: np.ndarray) -> list[np.ndarray]:
        y = C.resample(x, sr, 16000)
        return [y[int(s * 16000):int((s + step) * 16000)] for s in starts]

    P = {"mix": ast_scores(windows(mix_m))}
    for name in STEMS:
        if name in stems:
            P[name] = ast_scores(windows(C.mono(stems[name])))
    idx = {v: int(k) for k, v in _ast[1].config.id2label.items()}

    def best(label: str) -> np.ndarray:
        """Вероятность класса по окнам: лучший из микса и его честных стемов (по среднему)."""
        cands = [P["mix"][:, idx[label]]] + [P[s][:, idx[label]] for s in stems_for(label) if s in P]
        return max(cands, key=lambda c: float(c.mean()))

    def row(label: str, col: np.ndarray) -> list:
        return [label, RU.get(label, label), round(float(col.mean()), 3), round(float(col.max()), 3), round(float((col > 0.2).mean()), 2)]

    inst = sorted([row(n, best(n)) for n in INSTR if n in idx], key=lambda r: -r[2])
    inst = [r for r in inst if r[3] >= 0.15]
    timeline = []
    for wi, s in enumerate(starts):
        top = []
        for n in INSTR:
            if n not in idx:
                continue
            p = max([P["mix"][wi, idx[n]]] + [P[st][wi, idx[n]] for st in stems_for(n) if st in P])
            if p >= 0.25:
                top.append([RU.get(n, n), round(float(p), 2)])
        top.sort(key=lambda r: -r[1])
        timeline.append([round(float(s), 1), round(float(min(duration, s + step)), 1), top[:6]])
    vsrc = "vocals" if "vocals" in P else "mix"
    return {
        "instruments": inst,
        "timeline": timeline,
        "voice": [r for r in (row(n, P[vsrc][:, idx[n]]) for n in VOICE if n in idx) if r[3] >= 0.1],
        "genre": sorted([r for r in (row(n, P["mix"][:, idx[n]]) for n in GENRE if n in idx) if r[3] >= 0.08], key=lambda r: -r[2])[:8],
        "mood": sorted([row(n, P["mix"][:, idx[n]]) for n in MOOD if n in idx], key=lambda r: -r[2]),
        "music_p": round(float(P["mix"][:, idx["Music"]].mean()), 3) if "Music" in idx else None,
        "speech_p": round(float(P["mix"][:, idx["Speech"]].mean()), 3) if "Speech" in idx else None,
    }


# ---------- текст песни ----------

LANG_CODE = {"english": "en", "russian": "ru", "japanese": "ja", "german": "de", "french": "fr", "spanish": "es", "italian": "it",
             "korean": "ko", "chinese": "zh", "ukrainian": "uk", "portuguese": "pt", "polish": "pl", "turkish": "tr", "dutch": "nl",
             "swedish": "sv", "finnish": "fi", "czech": "cs", "latin": "la"}


def vocal_chunks(act: np.ndarray, fps: int = 10, max_len: float = 25.0) -> list[tuple[float, float]]:
    """Куски голоса для whisper: звучащие места, паузы короче 0,8 с склеены, длина до max_len (режется по самой длинной паузе)."""
    regs: list[list[float]] = []
    on = False
    for i, v in enumerate(act):
        if v and not on:
            regs.append([i / fps, i / fps])
            on = True
        elif not v and on:
            regs[-1][1] = i / fps
            on = False
    if on:
        regs[-1][1] = len(act) / fps
    merged: list[list[float]] = []
    for r in regs:
        if merged and r[0] - merged[-1][1] < 0.8:
            merged[-1][1] = r[1]
        else:
            merged.append(list(r))
    merged = [r for r in merged if r[1] - r[0] >= 0.4]
    chunks: list[tuple[float, float]] = []
    cur: list[list[float]] = []
    for r in merged:
        if cur and r[1] - cur[0][0] > max_len:
            chunks.append((cur[0][0], cur[-1][1]))
            cur = []
        cur.append(r)
        while cur and cur[-1][1] - cur[0][0] > max_len:      # один длинный кусок без пауз
            chunks.append((cur[0][0], cur[0][0] + max_len))
            cur[0][0] += max_len
            if cur[-1][1] - cur[0][0] <= 0.4:
                cur = []
    if cur:
        chunks.append((cur[0][0], cur[-1][1]))
    return chunks


def whisper_call(y16: np.ndarray, stt_url: str, language: str) -> dict:
    import soundfile as sf
    buf = io.BytesIO()
    sf.write(buf, y16, 16000, format="WAV", subtype="PCM_16")
    boundary = uuid.uuid4().hex
    fields = {"response_format": "verbose_json", "temperature": "0", "language": language, "prompt": "Song lyrics.",
              "max_context": "0"}
    body = io.BytesIO()
    for k, val in fields.items():
        body.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{val}\r\n".encode("utf-8"))
    body.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"vocals.wav\"\r\n"
               f"Content-Type: audio/wav\r\n\r\n".encode("utf-8"))
    body.write(buf.getvalue())
    body.write(f"\r\n--{boundary}--\r\n".encode("utf-8"))
    req = urllib.request.Request(stt_url.rstrip("/") + "/inference", data=body.getvalue(),
                                 headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def script_ok(tok: str, lang: str) -> bool:
    """Буквы не той письменности (иероглифы в английской песне) — выдумка whisper на крике."""
    for ch in tok:
        if not ch.isalpha():
            continue
        o = ord(ch)
        latin = o < 0x250 or 0x1E00 <= o < 0x1F00
        cyr = 0x400 <= o < 0x530
        if lang in ("en", "de", "fr", "es", "it", "pt", "pl", "tr", "nl", "sv", "fi", "cs", "la") and not latin:
            return False
        if lang in ("ru", "uk") and not (cyr or latin):
            return False
    return True


def lyrics_of(v: np.ndarray, sr: int, stt_url: str, language: str | None, vocal_act: np.ndarray,
              progress: C.Progress = C.no_progress) -> dict:
    """whisper-server Клавы по чистому голосу, кусками по паузам: слова с таймкодами. Куски в тишине, петли повторов и
    буквы чужой письменности — выдумки whisper, выкидываются."""
    y = C.resample(v, sr, 16000)
    chunks = vocal_chunks(vocal_act)
    if not chunks:
        return {"language": language or "", "words": [], "lines": []}
    lang = language
    if not lang:
        # язык — по двум самым длинным кускам
        votes: Counter = Counter()
        for c0, c1 in sorted(chunks, key=lambda c: c[0] - c[1])[:2]:
            r = whisper_call(y[int(max(0, c0 - 0.3) * 16000):int((c1 + 0.3) * 16000)], stt_url, "auto")
            votes[LANG_CODE.get(str(r.get("language", "")).lower(), str(r.get("language", "")))] += 1
        lang = votes.most_common(1)[0][0] if votes else "auto"
    words: list[list] = []
    lines: list[list] = []
    for ci, (c0, c1) in enumerate(chunks):
        progress(0.6 + 0.07 * ci / len(chunks), f"разбираю слова песни (whisper): кусок {ci + 1}/{len(chunks)}")
        a0 = max(0.0, c0 - 0.3)
        res = whisper_call(y[int(a0 * 16000):int((c1 + 0.3) * 16000)], stt_url, lang or "auto")
        for seg in res.get("segments", []):
            t0, t1 = a0 + float(seg.get("start", 0)), a0 + float(seg.get("end", 0))
            act = vocal_act[int(t0 * 10):max(int(t0 * 10) + 1, int(t1 * 10))]
            if act.size == 0 or act.mean() < 0.2 or float(seg.get("no_speech_prob", 0)) > 0.85 \
                    or float(seg.get("avg_logprob", 0)) < -1.3:
                continue
            seg_words: list[list] = []
            for w in seg.get("words", []):
                tok = str(w.get("word", ""))
                st = tok.strip()
                st = st.strip("*#♪♫")
                if not st or st.startswith("[") or not any(ch.isalpha() for ch in st) or not script_ok(st, lang or ""):
                    continue
                ws, we = a0 + float(w.get("start", 0)), a0 + float(w.get("end", 0))
                if seg_words and not tok.startswith(" ") and not st[0].isalnum():
                    seg_words[-1][2] += st                         # знак препинания — к слову
                elif seg_words and not tok.startswith(" "):
                    seg_words[-1][2] += st                         # продолжение слова (токены whisper — куски)
                    seg_words[-1][1] = round(we, 2)
                else:
                    seg_words.append([round(ws, 2), round(we, 2), st, round(float(w.get("probability", 0)), 2)])
            raw = str(seg.get("text", "")).strip()
            if not seg_words or raw[:1] in ("*", "(", "[", "#"):
                continue
            text = " ".join(w[2] for w in seg_words)
            # подряд одинаковые строки больше двух раз — петля whisper
            if len(lines) >= 2 and lines[-1][2] == text and lines[-2][2] == text:
                continue
            words.extend(seg_words)
            lines.append([round(seg_words[0][0], 2), round(seg_words[-1][1], 2), text])
    return {"language": lang or "", "words": words, "lines": lines}


# ---------- звук и сведение ----------

def mix_stats(mix: np.ndarray, stems: dict[str, np.ndarray], sr: int) -> dict:
    from scipy.signal import welch
    lufs, st = C.loudness(mix, sr)
    good = st[st > lufs - 20] if st.size else st
    lra = float(np.percentile(good, 95) - np.percentile(good, 10)) if good.size > 4 else 0.0
    m = C.mono(mix)
    peak = float(20 * np.log10(np.max(np.abs(mix)) + 1e-9))
    rms = float(20 * np.log10(np.sqrt(np.mean(m ** 2)) + 1e-9))
    f, pxx = welch(m, fs=sr, nperseg=8192)
    tot = pxx.sum() + 1e-18
    bands = {"sub": (20, 60), "bass": (60, 250), "lowmid": (250, 2000), "highmid": (2000, 6000), "air": (6000, 20000)}
    share = {k: round(float(pxx[(f >= a) & (f < b)].sum() / tot), 3) for k, (a, b) in bands.items()}
    centroid = float((f * pxx).sum() / tot)
    side = (mix[:, 0] - mix[:, 1]) / 2
    mid = (mix[:, 0] + mix[:, 1]) / 2
    width = float(10 * np.log10((np.mean(side ** 2) + 1e-12) / (np.mean(mid ** 2) + 1e-12)))
    se = {k: float(np.mean(v ** 2)) for k, v in stems.items()}
    st_tot = sum(se.values()) + 1e-18
    return {"lufs": round(lufs, 1), "lra": round(lra, 1), "peak_db": round(peak, 1), "crest_db": round(peak - rms, 1),
            "centroid_hz": round(centroid), "bands": share, "width_db": round(width, 1),
            "stem_share": {k: round(v / st_tot, 3) for k, v in se.items()},
            "short_term": [round(float(x), 1) for x in st]}


# ---------- паспорт для мозга и партитура ----------

def pct(p: float) -> str:
    return f"{int(round(p * 100))}%"


def describe_groove(dr: dict, tempo: dict) -> list[str]:
    out = []
    s8 = dr.get("swing8")
    if s8 is not None:
        out.append("восьмые прямые" if abs(s8) < 20 else "восьмые прямые, слабые доли чуть спешат" if s8 < 0
                   else f"свинг восьмых ~{int(s8)}% (100% — триольный шаффл)")
    s16 = dr.get("swing16")
    if s16 is not None and abs(s16) >= 15:
        out.append(f"свинг шестнадцатых ~{int(s16)}%")
    if dr.get("subdivision") == 3:
        out.append("доля дробится на три (триольная пульсация: 6/8, 12/8 или шаффл)")
    sb = dr.get("snare_backbeat_ms")
    if sb is not None:
        out.append(f"малый на 2 и 4 {'запаздывает' if sb > 0 else 'опережает'} на {abs(sb):.0f} мс"
                   + (" (оттяжка, грув «назад»)" if sb > 8 else " (подгоняет)" if sb < -8 else " (ровно в долю)"))
    tt = dr.get("tightness_ms")
    if tt is not None:
        out.append(f"разброс ударов вокруг сетки {tt:.0f} мс — " + ("квантовано, как у машины" if tt < 7 else
                                                                    "плотно, профессиональный барабанщик или лёгкая «живость»" if tt < 14 else "живая игра"))
    return out


def passport(an: dict) -> str:
    L: list[str] = []
    meta = an.get("meta", {})
    title = " — ".join(x for x in (meta.get("artist"), meta.get("title")) if x) or Path(an["source"]).stem
    L.append(f"ТРЕК: {title}. Длина {C.fmt_time(an['duration'])}. Это ИЗМЕРЕНИЯ слуха (нейросети-анализаторы), не догадки.")
    lab = an.get("labels", {})
    if lab.get("genre"):
        L.append("Жанр (AST AudioSet, средняя вероятность по окнам): " + ", ".join(f"{r[1]} {pct(r[2])}" for r in lab["genre"][:5]))
    if lab.get("mood"):
        L.append("Настроение: " + ", ".join(f"{r[1]} {pct(r[2])}" for r in lab["mood"][:4]))
    t = an["tempo"]
    if t.get("bpm"):
        st = t["stability"]
        feel = "метроном/сетка" if st < 0.012 else "живой, но ровный" if st < 0.035 else "гибкий темп (рубато, дышит)"
        drift = ""
        if t.get("start_bpm") and t.get("end_bpm") and abs(t["end_bpm"] - t["start_bpm"]) > 3:
            drift = f", от {t['start_bpm']:.0f} к {t['end_bpm']:.0f} BPM"
        fast = (f" Для медленной песни это может быть дробление доли: счёт в половину — {t['bpm'] / 2:.0f}, в треть — {t['bpm'] / 3:.0f} BPM."
                if t["bpm"] >= 150 else "")
        size = (f"размер: {t['meter']} доли в такте" if t.get("meter_sure", True)
                else f"размер не определился уверенно (чаще всего {t['meter']} долей между сильными, в {pct(t.get('meter_share', 0))} тактов)")
        L.append(f"Темп: {t['bpm']:.0f} BPM ({feel}, разброс долей {st * 100:.1f}%{drift}); {size}; тактов {len(an.get('bars', []))}.{fast}")
    k = an.get("key", {})
    if k.get("name"):
        L.append(f"Тональность: {k['name']} (уверенность {k.get('confidence', 0):.2f}, запасной вариант — {k.get('runner_up', '?')})"
                 + (f"; модуляции: {', '.join(f'{C.fmt_time(m[0])} → {m[1]}' for m in an.get('modulations', []))}" if an.get("modulations") else ""))
    if lab.get("instruments"):
        L.append("Инструменты (AST по миксу и стемам; средняя/пик/доля окон): " + ", ".join(
            f"{r[1]} {pct(r[2])}/{pct(r[3])}/{pct(r[4])}" for r in lab["instruments"][:12]))
    if an.get("sung"):
        L.append(f"Голос звучит {pct(an.get('voiced_share', 0))} времени трека (по тону чистого голоса)"
                 + ("; тип (AST, средняя/пик): " + ", ".join(f"{r[1]} {pct(r[2])}/{pct(r[3])}" for r in lab.get("voice", [])[:4])
                    if lab.get("voice") else ""))
    ms = an.get("mix_stats", {})
    if ms:
        b = ms["bands"]
        L.append(f"Звук: громкость {ms['lufs']} LUFS, динамика {ms['lra']} LU, пик {ms['peak_db']} dBFS, крест-фактор {ms['crest_db']} дБ; "
                 f"спектр: саб {pct(b['sub'])}, бас {pct(b['bass'])}, середина {pct(b['lowmid'])}, верхняя середина {pct(b['highmid'])}, "
                 f"воздух {pct(b['air'])}; центр тяжести спектра {ms['centroid_hz']} Гц; ширина стерео {ms['width_db']} дБ (стороны к центру); "
                 "доли стемов: " + ", ".join(f"{STEM_RU[k2]} {pct(v)}" for k2, v in ms["stem_share"].items()))
    dr = an.get("drums", {})
    g = describe_groove(dr, t) if dr else []
    if g:
        L.append("Грув: " + "; ".join(g))
    vm = an.get("vocal_melody", {})
    if vm.get("range"):
        L.append(f"Мелодия голоса: диапазон {vm['range']}, в среднем {vm['median']}; ходы: {vm['motion']}.")
    if an.get("theme"):
        L.append("Главная тема (первые ноты верхнего голоса самой повторяемой части): " + " ".join(an["theme"]))
    L.append("ФОРМА (части, аккорды по долям, ударные — типичный такт части шагами 16-х, x — удар):")
    rows = dr.get("bars", [])
    for s in an.get("sections", []):
        chs = [c[2] for c in an.get("chords", []) if c[1] > s["t0"] + 0.1 and c[0] < s["t1"] - 0.1]
        seq = []
        for c in chs:
            if not seq or seq[-1] != c:
                seq.append(c)
        prog = " ".join(seq[:16]) + (" …" if len(seq) > 16 else "")
        L.append(f" {C.fmt_time(s['t0'])}–{C.fmt_time(s['t1'])} {s['name']} [{s['label']}]"
                 f"{', голос' if s['vocals'] else ''}, громкость {s['energy']} дБ; аккорды: {prog or '—'}")
        a, b = s.get("bars", [0, 0])
        pat = common_pattern(rows, a, b)
        if pat:
            L.append(f"    бочка {pat['K']} | малый {pat['S']} | хэт {pat['H']} (совпадение тактов с рисунком {pct(pat['share'])})")
        words = [w[2] for w in an.get("lyrics", {}).get("words", []) if s["t0"] <= w[0] < s["t1"]]
        if words:
            txt = " ".join(words)
            L.append(f"    текст: «{txt[:220]}{'…' if len(txt) > 220 else ''}»")
    ly = an.get("lyrics", {})
    if ly.get("lines"):
        L.append(f"Текст целиком (whisper по чистому голосу, язык {ly.get('language')}; ошибки распознавания возможны):")
        full = " / ".join(l[2] for l in ly["lines"])
        L.append(full[:2500] + ("…" if len(full) > 2500 else ""))
    elif an.get("sung") is False:
        L.append("Слов нет: инструментал.")
    return "\n".join(L)


STEM_RU = {"vocals": "голос", "drums": "ударные", "bass": "бас", "other": "остальное"}


def score_text(an: dict) -> str:
    """«Трек в токенах»: по тактам — аккорды по долям, ударные 16-ми, бас и мелодия нотами (длина в 16-х), слова."""
    out = [f"# партитура: {an.get('meta', {}).get('title') or Path(an['source']).stem}; {an['tempo'].get('bpm')} BPM, "
           f"{an['tempo'].get('meter')}/4, {an.get('key', {}).get('name', '')}",
           "# ударные: K бочка, S малый, H хэт; ноты: имя+октава:длина в 16-х; '.' — пауза/нет удара"]
    beats = np.asarray(an.get("beats", []))
    chords = an.get("chords", [])
    rows = {r["bar"]: r for r in an.get("drums", {}).get("bars", [])}
    bass = an.get("notes", {}).get("bass", [])
    mel = an.get("notes", {}).get("vocals", []) or an.get("notes", {}).get("melody", [])
    words = an.get("lyrics", {}).get("words", [])
    sec_at = {s["bars"][0]: s["name"] for s in an.get("sections", []) if "bars" in s}

    def notes_in(ns, t0, t1, step):
        res = []
        for n in ns:
            if t0 <= n[0] < t1:
                d = max(1, int(round((n[1] - n[0]) / step)))
                res.append(f"{C.note_name(n[2])}:{d}")
        return " ".join(res) or "."

    for bi, bar in enumerate(an.get("bars", [])):
        t0 = bar[0]
        i_next = int(np.searchsorted(beats, bar[-1])) + 1
        t1 = float(beats[i_next]) if i_next < len(beats) else float(an["duration"])
        step = (t1 - t0) / (4 * len(bar))
        if bi in sec_at:
            out.append(f"## {sec_at[bi]}")
        chs = []
        for bt in bar:
            c = next((c[2] for c in chords if c[0] <= bt + 0.02 < c[1]), "N")
            chs.append(c if not chs or chs[-1] != c else "-")
        r = rows.get(bi)
        drum = f" K:{r['K']} S:{r['S']} H:{r['H']}" if r else ""
        ws = " ".join(w[2] for w in words if t0 <= w[0] < t1)
        out.append(f"такт {bi + 1} {C.fmt_time(t0)} | {' '.join(chs)} |{drum} | бас: {notes_in(bass, t0, t1, step)} | "
                   f"мелодия: {notes_in(mel, t0, t1, step)}" + (f" | «{ws}»" if ws else ""))
    return "\n".join(out)


def export_midi(an: dict, path: Path) -> None:
    import pretty_midi
    pm = pretty_midi.PrettyMIDI(initial_tempo=float(an["tempo"].get("bpm") or 120))
    drums = pretty_midi.Instrument(program=0, is_drum=True, name="Drums")
    for t, k in an.get("drums", {}).get("hits", []):
        drums.notes.append(pretty_midi.Note(velocity=100, pitch={"K": 36, "S": 38, "H": 42}[k], start=t, end=t + 0.08))
    pm.instruments.append(drums)
    prog_other = 0
    top = [r[0] for r in an.get("labels", {}).get("instruments", [])]
    for name, prog in (("Electric guitar", 29), ("Acoustic guitar", 25), ("Piano", 0), ("String section", 48), ("Violin, fiddle", 40),
                       ("Organ", 19), ("Synthesizer", 81), ("Orchestra", 48), ("Brass instrument", 61)):
        if name in top[:4]:
            prog_other = prog
            break
    for key, prog, nm in (("bass", 33, "Bass"), ("other", prog_other, "Other"), ("vocals", 53, "Vocal melody")):
        inst = pretty_midi.Instrument(program=prog, name=nm)
        for n in an.get("notes", {}).get(key, []):
            vel = int(np.clip(40 + 80 * (n[3] if len(n) > 3 else 0.7), 30, 120))
            inst.notes.append(pretty_midi.Note(velocity=vel, pitch=int(round(n[2])), start=float(n[0]), end=float(max(n[1], n[0] + 0.05))))
        if inst.notes:
            pm.instruments.append(inst)
    pm.write(str(path))


# ---------- весь разбор ----------

def analyze(path: str | Path, out_dir: Path | None = None, progress: C.Progress = C.no_progress, quick: bool = False,
            stt_url: str | None = "http://127.0.0.1:8097", language: str | None = None, force: bool = False) -> dict:
    src = Path(path)
    out = out_dir or C.track_dir(src)
    aj = out / "analysis.json"
    if aj.exists() and not force:
        try:
            old = json.loads(aj.read_text(encoding="utf-8"))
            if old.get("version") == VERSION and (not old.get("quick") or quick):
                progress(1.0, "этот трек уже разобран — беру готовое")
                return old
        except (json.JSONDecodeError, TypeError):
            pass
    out.mkdir(parents=True, exist_ok=True)
    t_start = time.time()
    progress(0.01, f"читаю {src.name}…")
    mix = C.decode(src)
    sr = C.SR
    duration = len(mix) / sr
    if duration < 3:
        raise RuntimeError("звука меньше трёх секунд — нечего слушать")
    mix_m = C.mono(mix)
    if all((out / f"{k}.flac").exists() for k in STEMS) and (out / "mix.flac").exists():
        progress(0.26, "дорожки уже поделены — беру готовые")
        stems = {k: C.read(out / f"{k}.flac") for k in STEMS}
    else:
        C.write(out / "mix.flac", mix)
        stems = separate(mix, progress)
        for k, v in stems.items():
            C.write(out / f"{k}.flac", v)
        C.free_gpu()

    progress(0.3, "ловлю доли и такты (beat_this)…")
    beats, downbeats = beats_of(mix_m, sr)
    beats, shift = align_beats(beats, C.mono(stems["drums"]), sr)
    if shift:
        downbeats = downbeats + shift
    bars, meter = bars_of(beats, downbeats, duration)
    tempo = tempo_info(beats, bars, meter)
    C.free_gpu()

    progress(0.36, "разбираю ударные на бочку, малый и хэт…")
    drums = drums_of(C.mono(stems["drums"]), sr, bars, beats) if bars else {}

    progress(0.42, "снимаю мелодию голоса (RMVPE)…")
    v_m = C.mono(stems["vocals"])
    f0 = vocal_f0(v_m, sr)
    np.save(out / "vocal_f0.npy", f0)
    C.free_gpu()
    vrms = frame_rms(v_m, sr, 10)
    vocal_act = (vrms > max(1e-4, np.percentile(vrms, 95) * 0.1)).astype(np.float32)
    von = onset_env(v_m, sr)
    vnotes = notes_from_f0(f0, von)
    sung_share = float(np.mean(f0 > 0))

    progress(0.5, "слушаю инструменты, жанр и настроение (AST)…")
    labels = labels_of(stems, mix_m, sr, duration)
    C.free_gpu()
    e_voc = float(np.mean(v_m ** 2))
    e_all = sum(float(np.mean(C.mono(x) ** 2)) for x in stems.values()) + 1e-12
    sung = sung_share > 0.08 and e_voc / e_all > 0.03

    lyrics = {"language": "", "words": [], "lines": []}
    if sung and stt_url:
        progress(0.6, "разбираю слова песни (whisper)…")
        try:
            lyrics = lyrics_of(v_m, sr, stt_url, language, vocal_act, progress)
        except (OSError, ValueError, json.JSONDecodeError) as e:
            C.log(f"текст не снялся: {e}")

    notes: dict[str, list] = {"vocals": vnotes}
    if not quick:
        progress(0.68, "перевожу бас в ноты (Basic Pitch)…")
        notes["bass"] = skyline(poly_notes(out / "bass.flac", 28, 420), lowest=True)
        progress(0.76, "перевожу гитары/клавиши/струнные в ноты (Basic Pitch)…")
        notes["other"] = poly_notes(out / "other.flac", 50, 3000, onset=0.55, frame=0.35)
        if not sung:
            notes["melody"] = skyline(notes["other"])
    else:
        notes["bass"], notes["other"] = [], []

    progress(0.84, "тональность и аккорды…")
    harm = C.mono(stems["bass"]) * 0.6 + C.mono(stems["other"])
    ch = beat_chroma(harm, sr, beats, duration) if len(beats) else np.zeros((12, 0))
    vch = beat_chroma(v_m, sr, beats, duration) if len(beats) else np.zeros((12, 0))
    bch = beat_chroma(C.mono(stems["bass"]), sr, beats, duration, bass=True) if len(beats) else np.zeros((12, 0))
    key = key_of(ch + 0.5 * vch)
    beat_pos = np.zeros(len(beats), dtype=np.int32)
    for bar in bars:
        i = int(np.argmin(np.abs(beats - bar[0])))
        beat_pos[i:i + len(bar)] = np.arange(len(bar))[: len(beats) - i]
    chords = chords_of(ch, bch, beats, beat_pos, meter, key, duration)
    key = settle_key(key, chords)

    progress(0.9, "форма: куплеты, припевы, части…")
    sections = sections_of(mix_m, sr, ch + 0.3 * vch, beats, bars, vocal_act, duration, sung, lyrics.get("words"))
    modulations = local_keys(sections, ch + 0.5 * vch, beats, key)

    an = {"version": VERSION, "quick": quick, "source": str(src.resolve()), "meta": C.tags(src), "duration": round(duration, 3),
          "sr": sr, "dir": str(out), "mix": "mix.flac", "stems": {k: f"{k}.flac" for k in stems},
          "beats": [round(float(b), 3) for b in beats], "downbeats": [round(float(b), 3) for b in downbeats],
          "bars": [[round(float(x), 3) for x in b] for b in bars], "tempo": tempo, "drums": drums, "key": key,
          "modulations": modulations, "chords": chords, "sections": sections, "labels": labels, "lyrics": lyrics,
          "notes": notes, "sung": sung, "voiced_share": round(sung_share, 3), "vocal_f0": "vocal_f0.npy", "beat_shift": round(shift, 4),
          "vocal_melody": melody_summary(vnotes), "mix_stats": mix_stats(mix, stems, sr)}
    an["theme"] = theme_of(an)
    an["seconds"] = round(time.time() - t_start, 1)
    progress(0.96, "пишу паспорт, партитуру и MIDI…")
    (out / "passport.txt").write_text(passport(an), encoding="utf-8")
    (out / "score.txt").write_text(score_text(an), encoding="utf-8")
    try:
        export_midi(an, out / "transcription.mid")
        an["midi"] = "transcription.mid"
    except (ValueError, OSError) as e:
        C.log(f"MIDI не записался: {e}")
    C.save_json(aj, an)
    progress(1.0, f"разобрала за {an['seconds']:.0f} с")
    return an


def settle_key(key: dict, chords: list[list]) -> dict:
    """Мажор или параллельный минор (одни ноты!) решают аккорды: чей тоника звучит дольше, та и тональность."""
    if not chords or "pc" not in key:
        return key
    dur: Counter = Counter()
    for t0, t1, name in chords:
        dur[name.split("/")[0]] += t1 - t0
    pc = key["pc"]
    # квинта без терции (A5) — голос за свою тонику в любом ладу
    if key["mode"] == "major":
        alt_pc, alt_mode = (pc + 9) % 12, "minor"
        own = dur[C.NOTE_NAMES[pc]] + dur[C.NOTE_NAMES[pc] + "maj7"] + dur[C.NOTE_NAMES[pc] + "5"]
        alt = dur[C.NOTE_NAMES[alt_pc] + "m"] + dur[C.NOTE_NAMES[alt_pc] + "m7"] + dur[C.NOTE_NAMES[alt_pc] + "5"]
    else:
        alt_pc, alt_mode = (pc + 3) % 12, "major"
        own = dur[C.NOTE_NAMES[pc] + "m"] + dur[C.NOTE_NAMES[pc] + "m7"] + dur[C.NOTE_NAMES[pc] + "5"]
        alt = dur[C.NOTE_NAMES[alt_pc]] + dur[C.NOTE_NAMES[alt_pc] + "maj7"] + dur[C.NOTE_NAMES[alt_pc] + "5"]
    # те же ноты у параллельных тональностей: профиль разводит их слабо — решают аккорды
    close = key["confidence"] - key.get("all", {}).get(f"{alt_mode}{alt_pc}", -1) < 0.15
    if close and alt > own * 1.5 and alt > 8:
        k2 = dict(key)
        k2.update({"tonic": C.NOTE_NAMES[alt_pc], "mode": alt_mode, "pc": alt_pc, "name": f"{C.NOTE_NAMES[alt_pc]} {KEY_RU[alt_mode]}",
                   "runner_up": key["name"], "by_chords": True})
        return k2
    return key


def local_keys(sections: list[dict], ch: np.ndarray, beats: np.ndarray, key: dict) -> list[list]:
    """Модуляции: тональность каждой части длиннее 15 с; уверенно другая, чем общая, — смена."""
    out = []
    cur = key.get("name")
    for s in sections:
        if s["t1"] - s["t0"] < 15:
            continue
        m = (beats >= s["t0"]) & (beats < s["t1"])
        if m.sum() < 8:
            continue
        k = key_of(ch[:, m])
        parallel = k["tonic"] == key.get("tonic") and k["mode"] != key.get("mode")
        if k["name"] != cur and k["confidence"] > 0.8 and k["margin"] > (0.15 if parallel else 0.08):
            out.append([s["t0"], k["name"]])
            cur = k["name"]
        elif k["name"] == key.get("name"):
            cur = k["name"]
    return out


def melody_summary(notes: list[list[float]]) -> dict:
    if len(notes) < 8:
        return {}
    ps = np.array([n[2] for n in notes])
    lo, hi = np.percentile(ps, 3), np.percentile(ps, 97)
    steps = np.abs(np.diff(np.round(ps)))
    stepwise = float(np.mean(steps <= 2))
    leaps = float(np.mean(steps >= 5))
    return {"range": f"{C.note_name(lo)}–{C.note_name(hi)} ({int(round(hi - lo))} полутонов)", "median": C.note_name(np.median(ps)),
            "motion": f"поступенно {pct(stepwise)}, скачки на кварту и больше {pct(leaps)}", "lo": float(lo), "hi": float(hi),
            "median_midi": float(np.median(ps))}


def theme_of(an: dict) -> list[str]:
    """Первые ноты мелодии припева (у инструментала — самой повторяемой части): узнать знакомую тему по нотам."""
    mel = an["notes"].get("vocals") if an.get("sung") else an["notes"].get("melody")
    if not mel:
        return []
    secs = an.get("sections", [])
    start = next((s["t0"] for s in secs if s.get("name", "").startswith("припев")), None)
    if start is None:
        cnt = Counter(s["label"] for s in secs)
        lab = cnt.most_common(1)[0][0] if cnt else None
        start = next((s["t0"] for s in secs if s["label"] == lab), 0.0)
    names = [C.note_name(n[2]) for n in mel if n[0] >= start]
    out: list[str] = []
    for nm in names:
        if out and out[-1].split("×")[0] == nm:
            base, _, k = out[-1].partition("×")
            out[-1] = f"{base}×{int(k or 1) + 1}"
        else:
            out.append(nm)
        if len(out) >= 20:
            break
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--quick", action="store_true", help="без Basic Pitch (быстрее; для «послушай, что играет»)")
    ap.add_argument("--lang", default=None)
    ap.add_argument("--stt", default="http://127.0.0.1:8097")
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args()
    an = analyze(a.file, quick=a.quick, stt_url=a.stt, language=a.lang, force=a.force)
    print(Path(an["dir"]) / "passport.txt")
    print((Path(an["dir"]) / "passport.txt").read_text(encoding="utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
