"""Мелодия на замок: голос ACE встаёт по времени на строки певицы и садится на её ноты.

Замер 30.09 на «Lately» (ACE-кавер v4, по строкам): в полутоне от нот певицы только 36 % кадров, 45 % — дальше трёх
полутонов; со второй половины песни ACE уходит в свою линию (медиана 3,5–5,5 полутона мимо) и начинает строки на
1–2 с раньше минуса. Друг: «сделай мелодично, чтобы в ноты попадала и под мелодию». Поэтому после ACE:

1. Где что спето — по словам, а не по времени: whisper (время каждого слова) против строк перевода. Замер: ACE поёт
   строки не там, где певица, повторяет и пропускает их — раскладка фраз по времени отдавала строкам чужие слова
   (0,70 ошибок по словам на строку ещё ДО замка). Границы строки — по звуку (whisper режет протяжную ноту).
   Вариантов ACE несколько (сиды, модели) — каждая строка берётся из того, где она внятнее и ближе к регистру певицы
   (сведение дублей, как в студии; тембр потом всё равно один — Seed-VC Клавы).
2. Время: строка встаёт на строку певицы; внутри — DTW по звучанию, громкости и атакам слогов голоса ACE против голоса певицы — слоги встают на
   её ноты (перевод и так слог в слог); наклон сглажен и ограничен 0,6–1,7, вся группа — 0,75–1,35 раза, иначе
   ровная растяжка.
3. Тон: ноты певицы (RMVPE, вибрато сглажено, сетка её строя); октава — общая на песню, фраза уходит на соседнюю,
   только если ACE явно поёт её октавой выше/ниже (замер: поздние припевы ACE брал на 7–9 полутонов выше Леры —
   тянуть их на октаву вниз PSOLA-й нельзя, выходит мелодия октавой выше); вибрато и подъезды ACE остаются —
   сдвигается только центр ноты (~50 мс). Нет ноты рядом — ближайшая ступень лада.

Замер на голосе ACE v4 (без Seed-VC): в полутоне от нот певицы 0,35 → 0,71–0,79, покрытие её фраз 0,52 → 0,63 (DTW),
ошибок по словам 0,28 → 0,36–0,43 (whisper сам гуляет на ±0,04) — PSOLA на сдвигах в 2–4 полутона чуть смазывает
согласные; поэтому судья кавера теперь берёт вариант ACE, который поёт ближе к регистру певицы.
4. PSOLA Praat (overlap-add): форманты не плывут, тон и время — одной пересинтезой.

    tts\\.venv\\Scripts\\python.exe music\\tune.py <папка трека> <выход.wav> <голос варианта.wav>… --lyrics ru.txt [--from 0] [--to 0]
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import analyze as A  # noqa: E402
import common as C  # noqa: E402
import cover as CV  # noqa: E402

FPS = 100               # кадров в секунду (RMVPE, огибающие)
MAX_SHIFT = 6.0         # полутонов: дальше — не та нота сопоставилась (или PSOLA уже слышно), берём ступень лада
WARP_SLACK = 0.35       # с: DTW увёл слоги дальше этого от ровной растяжки — не верим, растягиваем ровно
STRETCH = (0.75, 1.35)  # во сколько раз можно растянуть группу фраз под строки певицы
SLOPE = (0.6, 1.7)      # местный наклон времени внутри группы
KNOT = 20               # кадров между опорами времени (0,2 с)
MAJOR = (0, 2, 4, 5, 7, 9, 11)
MINOR = (0, 2, 3, 5, 7, 8, 10)


def to_midi(f: np.ndarray) -> np.ndarray:
    m = np.full(len(f), np.nan)
    v = f > 0
    m[v] = 69 + 12 * np.log2(f[v] / 440.0)
    return m


def tuning(m: np.ndarray) -> float:
    """Строй записи: на сколько (в полутонах, −0,5…0,5) ноты певицы в среднем отстоят от сетки A=440."""
    x = m[~np.isnan(m)]
    if len(x) < 50:
        return 0.0
    ang = 2 * np.pi * (x - np.round(x))
    return float(np.arctan2(np.mean(np.sin(ang)), np.mean(np.cos(ang))) / (2 * np.pi))


def smooth_runs(m: np.ndarray, width: int) -> np.ndarray:
    """Медиана по звучащим кускам: вибрато и дрожь уходят, переходы между нотами остаются."""
    from scipy.ndimage import median_filter
    out = np.full(len(m), np.nan)
    for a, b in CV.runs(~np.isnan(m)):
        out[a:b] = median_filter(m[a:b], size=min(width, b - a), mode="nearest")
    return out


def notes(m: np.ndarray, tune: float, min_len: int = 6) -> np.ndarray:
    """Ноты певицы по кадрам: сглаженный тон к сетке её строя; обрывки короче min_len кадров — к соседней ноте."""
    q = smooth_runs(m, 9)
    ok = ~np.isnan(q)
    q[ok] = np.round(q[ok] - tune) + tune
    for a, b in CV.runs(ok):
        seg = q[a:b]
        i = 0
        while i < len(seg):
            j = i
            while j < len(seg) and seg[j] == seg[i]:
                j += 1
            if j - i < min_len and (i > 0 or j < len(seg)):
                seg[i:j] = seg[i - 1] if i > 0 else seg[j]
            i = j
    return q


def scale_snap(center: float, tune: float, tonic: int, minor: bool) -> float:
    """Ближайшая к center ступень лада (с учётом строя записи)."""
    steps = MINOR if minor else MAJOR
    base = np.floor(center - tune)
    cands = [base + d + tune for d in range(-2, 4) if int(base + d - tonic) % 12 in steps]
    return float(min(cands, key=lambda c: abs(c - center)))


# ---------- фразы и строки ----------

# ---------- время ----------

def features(y: np.ndarray, sr: int, f0: np.ndarray) -> np.ndarray:
    """Кадры 10 мс: звучит ли голос, громкость (z), атака слога (0…1) — по ним слоги ACE встают на ноты певицы."""
    n = len(f0)
    rms = A.frame_rms(y, sr, FPS)
    rms = np.pad(rms, (0, max(0, n - len(rms))))[:n]
    le = np.log(rms + 1e-5)
    le = (le - le.mean()) / (le.std() + 1e-6)
    on = A.onset_env(y, sr, FPS)
    on = np.pad(on, (0, max(0, n - len(on))))[:n]
    on = np.clip(on / (np.percentile(on, 95) + 1e-6), 0, 1)
    return np.stack([(f0 > 0).astype(float), 0.5 * le, 0.7 * on], axis=1)


def dtw_map(fa: np.ndarray, fo: np.ndarray) -> np.ndarray:
    """Для каждого кадра ACE — дробный кадр певицы (DTW в полосе вокруг ровной растяжки)."""
    import librosa
    cost = np.abs(fa[:, None, :] - fo[None, :, :]).sum(-1)
    _, wp = librosa.sequence.dtw(C=cost, step_sizes_sigma=np.array([[1, 1], [0, 1], [1, 0]]),
                                 weights_add=np.array([0.0, 0.3, 0.3]), global_constraints=True, band_rad=0.25)
    wp = wp[::-1]
    s = np.zeros(len(fa))
    c = np.zeros(len(fa))
    np.add.at(s, wp[:, 0], wp[:, 1])
    np.add.at(c, wp[:, 0], 1)
    return s / np.maximum(c, 1)


@dataclass
class Warp:
    """Время куска ACE → время песни: опоры taus (кадры от s) → ts (кадры от o0), до s и после e — крайние наклоны."""
    s: float
    e: float
    o0: float
    taus: list[float] = field(default_factory=list)
    ts: list[float] = field(default_factory=list)

    @property
    def first(self) -> float:
        return (self.ts[1] - self.ts[0]) / max(1e-6, self.taus[1] - self.taus[0])

    @property
    def last(self) -> float:
        return (self.ts[-1] - self.ts[-2]) / max(1e-6, self.taus[-1] - self.taus[-2])

    @property
    def end(self) -> float:
        return self.o0 + self.ts[-1] / FPS

    def at(self, tau: float) -> float:
        if tau <= self.s:
            return self.o0 - (self.s - tau) * self.first
        if tau >= self.e:
            return self.end + (tau - self.e) * self.last
        return self.o0 + float(np.interp((tau - self.s) * FPS, self.taus, self.ts)) / FPS

    def duration_points(self, a_in: float, b_in: float) -> list[tuple[float, float]]:
        """Точки DurationTier Praat (время от начала куска, во сколько раз длиннее): кусочно-постоянный наклон."""
        pts = [(0.0, self.first)]
        for k in range(len(self.taus) - 1):
            f = (self.ts[k + 1] - self.ts[k]) / max(1e-6, self.taus[k + 1] - self.taus[k])
            pts.append((self.s - a_in + self.taus[k] / FPS + 1e-4, f))
            pts.append((self.s - a_in + self.taus[k + 1] / FPS - 1e-4, f))
        pts.append((b_in - a_in, self.last))
        return pts


def uniform(s: float, e: float, o0: float, ratio: float) -> Warp:
    n = (e - s) * FPS
    return Warp(s, e, o0, [0.0, n], [0.0, n * ratio])


def smooth_warp(s: float, e: float, o0: float, mp: np.ndarray, total: float) -> Warp:
    """Путь DTW → опоры каждые KNOT кадров, наклон в SLOPE, концы: s → o0, e → o0 + total."""
    n = len(mp)
    taus = [float(k) for k in range(0, n, KNOT)]
    if n - 1 - taus[-1] < KNOT / 2 and len(taus) > 1:
        taus.pop()
    taus.append(float(n))
    ts = [0.0]
    for k in range(1, len(taus) - 1):
        lo, hi = int(taus[k]) - KNOT // 2, int(taus[k]) + KNOT // 2
        want = float(np.mean(mp[max(0, lo):min(n, hi)]))
        d = taus[k] - taus[k - 1]
        ts.append(float(np.clip(want, ts[-1] + SLOPE[0] * d, ts[-1] + SLOPE[1] * d)))
    ts.append(max(total, ts[-1] + SLOPE[0] * (taus[-1] - taus[-2])))
    ts = list(np.array(ts) * (total / ts[-1]))   # ровно в конец строки певицы
    return Warp(s, e, o0, taus, ts)


# ---------- тон ----------

def psola(y: np.ndarray, sr: int, shift_at, dur_points: list[tuple[float, float]]) -> np.ndarray:
    """Praat: тон каждой точки × 2^(сдвиг/12), длительность по DurationTier, overlap-add."""
    import parselmouth
    from parselmouth.praat import call
    snd = parselmouth.Sound(y.astype(np.float64), sampling_frequency=sr)
    manip = call(snd, "To Manipulation", 0.01, 70, 1100)
    pt = call(manip, "Extract pitch tier")
    x0, x1 = snd.xmin, snd.xmax
    new = call("Create PitchTier", "tuned", x0, x1)
    for i in range(1, int(call(pt, "Get number of points")) + 1):
        t = call(pt, "Get time from index", i)
        call(new, "Add point", t, call(pt, "Get value at index", i) * 2 ** (shift_at(t) / 12))
    call([new, manip], "Replace pitch tier")
    if any(abs(f - 1) > 1e-3 for _, f in dur_points):
        dt = call("Create DurationTier", "stretch", x0, x1)
        for t, f in dur_points:
            call(dt, "Add point", min(max(t, x0), x1), f)
        call([dt, manip], "Replace duration tier")
    out = call(manip, "Get resynthesis (overlap-add)")
    return np.asarray(out.values[0], dtype=np.float32)


def shift_curve(center: np.ndarray, tgt: np.ndarray, octave: int, tune: float, tonic: int, minor: bool) -> tuple[np.ndarray, dict]:
    """Сдвиг (полутоны) на каждый кадр: центр ноты ACE → нота певицы (+ октава песни); нет ноты или сдвиг больше
    MAX_SHIFT — ступень лада. Сглажено ~50 мс: переход между нотами — быстрый подъезд, а не ступенька."""
    from scipy.ndimage import median_filter, uniform_filter1d
    shift = np.zeros(len(center))
    locked = snapped = 0
    for i in np.flatnonzero(~np.isnan(center)):
        s = tgt[i] + 12 * octave - center[i] if not np.isnan(tgt[i]) else np.nan
        if np.isnan(s) or abs(s) > MAX_SHIFT:
            s = scale_snap(center[i], tune, tonic, minor) - center[i]
            snapped += 1
        else:
            locked += 1
        shift[i] = s
    for a, b in CV.runs(~np.isnan(center)):
        if b - a >= 3:
            shift[a:b] = uniform_filter1d(median_filter(shift[a:b], size=min(5, b - a), mode="nearest"), size=5, mode="nearest")
    voiced = ~np.isnan(center)
    return shift, {"locked": locked, "snapped": snapped,
                   "mean_shift": round(float(np.mean(np.abs(shift[voiced]))) if voiced.any() else 0.0, 2)}


# ---------- что где спето: слова whisper → строки ----------

def heard_words(v: np.ndarray, sr: int, stt_url: str) -> list[tuple[str, float, float]]:
    """Слова голоса с временем (с от начала куска): whisper по звучащим кускам, токены склеиваются в слова."""
    import re
    y16 = C.resample(v, sr, 16000)
    act = A.frame_rms(v, sr, 10)
    act = act > max(1e-4, np.percentile(act, 90) * 0.12) if len(act) else act
    out: list[tuple[str, float, float]] = []
    for c0, c1 in A.vocal_chunks(act):
        r = A.whisper_call(y16[int(c0 * 16000):int(c1 * 16000)], stt_url, "ru")
        for seg in r.get("segments", []):
            if float(seg.get("no_speech_prob", 0) or 0) > 0.6:
                continue
            cur: list | None = None
            for tok in seg.get("words") or []:
                txt = str(tok.get("word", ""))
                letters = re.sub(r"[^а-яa-z]", "", txt.lower().replace("ё", "е"))
                t0, t1 = c0 + float(tok.get("start", 0)), c0 + float(tok.get("end", 0))
                if not letters:   # знак препинания закрывает слово
                    if cur:
                        out.append(tuple(cur))
                    cur = None
                elif txt.startswith(" ") or cur is None:
                    if cur:
                        out.append(tuple(cur))
                    cur = [letters, t0, t1]
                else:
                    cur[0] += letters
                    cur[2] = t1
            if cur:
                out.append(tuple(cur))
    return out


def similar(a: str, b: str) -> float:
    """Похожесть слов 0…1 по расстоянию Левенштейна (whisper на пении путает окончания и буквы)."""
    if a == b:
        return 1.0
    if not a or not b:
        return 0.0
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return 1 - prev[-1] / max(len(a), len(b))


def align_rows(hyp: list[tuple[str, float, float]], row_words: list[list[str]]) -> list[tuple[float, float, float] | None]:
    """Слова, которые услышал whisper, против слов строк (по порядку, выравнивание с правкой): для каждой строки —
    (доля её слов, что спеты, начало, конец) или None, если строку не узнать. Лишнее у whisper (выдумки на
    проигрыше, повторы ACE) — дёшево пропускается."""
    ref = [(w, r) for r, ws in enumerate(row_words) for w in ws]
    n, m = len(ref), len(hyp)
    d = np.zeros((n + 1, m + 1))
    d[:, 0] = np.arange(n + 1) * 1.0
    d[0, :] = np.arange(m + 1) * 0.5
    sims = np.array([[similar(ref[i][0], hyp[j][0]) for j in range(m)] for i in range(n)]) if n and m else np.zeros((n, m))
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            s = sims[i - 1, j - 1]
            d[i, j] = min(d[i - 1, j] + 1.0, d[i, j - 1] + 0.5, d[i - 1, j - 1] + (1 - s if s >= 0.34 else 1.2))
    got: dict[int, list[tuple[float, float, float]]] = {}
    i, j = n, m
    while i > 0 and j > 0:
        s = sims[i - 1, j - 1]
        if d[i, j] == d[i - 1, j - 1] + (1 - s if s >= 0.34 else 1.2):
            if s >= 0.5:
                got.setdefault(ref[i - 1][1], []).append((s, hyp[j - 1][1], hyp[j - 1][2]))
            i, j = i - 1, j - 1
        elif d[i, j] == d[i - 1, j] + 1.0:
            i -= 1
        else:
            j -= 1
    res: list[tuple[float, float, float] | None] = []
    for r, ws in enumerate(row_words):
        g = got.get(r, [])
        score = sum(x[0] for x in g) / max(1, len(ws))
        res.append((score, min(x[1] for x in g), max(x[2] for x in g)) if g and score >= 0.35 else None)
    return res


def voiced_mask(y: np.ndarray, sr: int) -> np.ndarray:
    rms = A.frame_rms(y, sr, FPS)
    return rms > max(1e-4, np.percentile(rms, 90) * 0.1) if len(rms) else np.zeros(0, bool)


def snap(act: np.ndarray, t0: float, t1: float, lo: float, hi: float) -> tuple[float, float]:
    """Границы строки по звуку: whisper ставит время слов грубо (±0,3 с) и обрезает протяжную последнюю ноту —
    начало тянется назад до начала звука (не дальше 0,5 с и не за lo), конец — вперёд до конца звука (до 1,2 с, не за hi)."""
    n = len(act)
    a = int(t0 * FPS)
    b = int(t1 * FPS)
    lo_f, hi_f = max(0, int(lo * FPS)), min(n, int(hi * FPS))
    k = a
    if 0 <= k < n and not act[k]:   # слово начато в тишине — к первому звуку рядом
        k2 = next((q for q in range(k, min(n, k + 30)) if act[q]), k)
        k = k2
    while k - 1 >= max(lo_f, a - 50) and 0 <= k - 1 < n and act[k - 1]:
        k -= 1
    e = min(b, n - 1)
    while e + 1 < min(hi_f, b + 120) and act[e + 1]:
        e += 1
    return k / FPS, max(k + 1, e + 1) / FPS


@dataclass
class Take:
    """Что поставить: кусок голоса варианта src [s, e] (с куска) → строки певицы rows=(с, по) на [o0, o1].
    lo/hi — докуда можно брать вдох и хвост, не залезая в соседнюю строку этого же варианта."""
    src: int
    s: float
    e: float
    o0: float
    o1: float
    rows: tuple[int, int]
    score: float = 0.0
    lo: float = 0.0
    hi: float = 0.0


def takes_by_words(voices: list[np.ndarray], heard: list[list[tuple[str, float, float]]], row_words: list[list[str]],
                   rows: list[tuple[float, float]], sr: int, f0o: np.ndarray, ov: np.ndarray | None) -> list[Take]:
    """Сведение лучших дублей: каждая строка берётся из того варианта ACE, где она спета внятнее всего и ближе к
    регистру певицы (Seed-VC потом всё равно перекрашивает в тембр Клавы — дубли разных сидов не слышно).
    Строку не спел никто — её нет (отчёт скажет)."""
    mo = to_midi(f0o)
    on = notes(mo, tuning(mo))
    act_o = voiced_mask(ov, sr) if ov is not None else None
    per_row: list[list[Take]] = [[] for _ in rows]
    for vi, (v, hw) in enumerate(zip(voices, heard)):
        found = align_rows(hw, row_words)
        act = voiced_mask(v, sr)
        idx = [r for r, f in enumerate(found) if f is not None]
        for k, r in enumerate(idx):
            sc, t0, t1 = found[r]
            lo = found[idx[k - 1]][2] if k > 0 else 0.0
            hi = found[idx[k + 1]][1] if k + 1 < len(idx) else len(v) / sr
            s, e = snap(act, t0, t1, lo, hi)
            per_row[r].append(Take(vi, s, e, rows[r][0], rows[r][1], (r, r + 1), sc, lo, hi))
    out: list[Take] = []
    prev_src = -1
    for r, cands in enumerate(per_row):
        if not cands:
            continue
        o0, o1 = rows[r]
        if act_o is not None:   # строка певицы — тоже по звуку, между соседними строками
            lo = rows[r - 1][1] if r > 0 else 0.0
            hi = rows[r + 1][0] if r + 1 < len(rows) else len(act_o) / FPS
            o0, o1 = snap(act_o, o0, o1, lo, hi)

        def value(t: Take) -> float:
            seg = voices[t.src][int(t.s * sr):int(t.e * sr)]
            c = smooth_runs(to_midi(A.vocal_f0(seg, sr)), 15)
            ratio = (o1 - o0) / max(0.1, t.e - t.s)
            tg = np.array([on[q] if 0 <= (q := int(o0 * FPS + i * ratio)) < len(on) else np.nan for i in range(len(c))])
            both = ~np.isnan(c) & ~np.isnan(tg)
            d = c[both] - tg[both]
            if len(d) < 10:
                return t.score * 0.8
            # близко к нотам певицы в любой октаве — меньше правки; в её же октаве — ещё и без скачков регистра
            near = float(np.mean(np.abs(d - 12 * np.round(np.median(d) / 12)) < 3))
            same = float(np.mean(np.abs(d) < 3))
            fits = 1.0 if STRETCH[0] <= ratio <= STRETCH[1] else 0.6
            return (t.score + 0.2 * near + 0.2 * same) * fits + (0.05 if t.src == prev_src else 0)

        best = max(cands, key=value)
        best.o0, best.o1 = o0, o1
        prev_src = best.src
        out.append(best)
    return out


# ---------- голос на замок ----------

def render(voices: list[np.ndarray], takes: list[Take], sr: int, f0o: np.ndarray, key: dict, ov: np.ndarray | None = None,
           progress: C.Progress = C.no_progress, timing: str = "dtw", pitch: bool = True,
           trace: list | None = None) -> tuple[np.ndarray, dict]:
    """Поставить дубли на время строк певицы и посадить на её ноты. voices — голоса вариантов ACE (моно, время куска),
    f0o — тон певицы на то же время (10 мс), ov — её голос для DTW слогов (None — ровная растяжка)."""
    mo = to_midi(f0o)
    tune = tuning(mo)
    on = notes(mo, tune)
    tonic = int(key.get("pc", 0))
    minor = key.get("mode") == "minor"
    n_out = max(len(v) for v in voices)
    pre, post = 0.15, 0.25   # вдох и тихие согласные в начале (с, ф, ш), хвост ноты — ниже порога громкости
    prepared = []
    diffs: list[np.ndarray] = []
    for tk in takes:
        v = voices[tk.src]
        s, e, o0, o1 = tk.s, tk.e, tk.o0, tk.o1
        ratio = (o1 - o0) / max(0.1, e - s)
        w = uniform(s, e, o0, float(np.clip(ratio, *STRETCH)))
        if timing in ("none", "shift"):   # на месте / только сдвиг к строке, без растяжки
            w = uniform(s, e, s if timing == "none" else o0, 1.0)
        elif timing == "dtw" and ov is not None and STRETCH[0] <= ratio <= STRETCH[1] and e - s >= 0.5:
            fa = features(v[int(s * sr):int(e * sr)], sr, A.vocal_f0(v[int(s * sr):int(e * sr)], sr))
            fo = features(ov[int(o0 * sr):int(o1 * sr)], sr, f0o[int(o0 * FPS):int(o0 * FPS) + int((o1 - o0) * FPS) + 1])
            if len(fa) >= 20 and len(fo) >= 20:
                dw = smooth_warp(s, e, o0, dtw_map(fa, fo), (o1 - o0) * FPS)
                flat = np.array(dw.taus) * (dw.ts[-1] / dw.taus[-1])
                if np.max(np.abs(np.array(dw.ts) - flat)) / FPS <= WARP_SLACK:   # DTW увёл слоги — не верим
                    w = dw
        a_in = max(0.0, s - pre, tk.lo + 0.02)
        b_in = min(len(v) / sr, e + post, max(e, tk.hi - 0.02))
        seg = v[int(a_in * sr):int(b_in * sr)]
        if len(seg) < sr // 10:
            continue
        center = smooth_runs(to_midi(A.vocal_f0(seg, sr)), 15)
        tgt = np.full(len(center), np.nan)
        for k in np.flatnonzero(~np.isnan(center)):
            idx = int(round(w.at(a_in + k / FPS) * FPS))
            if not (0 <= idx < len(on)):
                continue
            if not np.isnan(on[idx]):
                tgt[k] = on[idx]
                continue
            lo, hi = max(0, idx - 30), min(len(on), idx + 31)   # нота певицы рядом (подъезд, хвост) — держать её
            near = np.flatnonzero(~np.isnan(on[lo:hi]))
            if len(near):
                tgt[k] = on[lo + near[np.argmin(np.abs(near + lo - idx))]]
        both = ~np.isnan(center) & ~np.isnan(tgt)
        diffs.append(center[both] - tgt[both])
        prepared.append((tk, w, a_in, b_in, seg, center, tgt))
    all_d = np.concatenate(diffs) if diffs else np.zeros(0)
    octave = int(np.round(np.median(all_d) / 12)) if len(all_d) >= 50 else 0

    def octave_for(center: np.ndarray, tgt: np.ndarray) -> int:
        """Октава строки: общая, если только соседняя не даёт заметно меньший сдвиг (ACE спел её октавой выше/ниже)."""
        both = ~np.isnan(center) & ~np.isnan(tgt)
        if both.sum() < 10:
            return octave
        d = center[both] - tgt[both]
        cost = {o: float(np.mean(np.minimum(np.abs(d - 12 * o), MAX_SHIFT + 2))) + (0 if o == octave else 1.5)
                for o in (octave - 1, octave, octave + 1)}
        return min(cost, key=cost.get)

    out = np.zeros(int(n_out * STRETCH[1]) + sr, np.float32)
    report: list[dict] = []
    for n_job, (tk, w, a_in, b_in, seg, center, tgt) in enumerate(prepared):
        progress(n_job / max(1, len(prepared)), f"ставлю голос на мелодию: строка {n_job + 1}/{len(prepared)}")
        oct_here = octave_for(center, tgt)
        shift, rep = shift_curve(center, tgt, oct_here, tune, tonic, minor)
        if not pitch:
            shift[:] = 0.0
        both = ~np.isnan(center) & ~np.isnan(tgt)
        if both.sum() >= 10:
            rep["vs_singer"] = [round(float(x), 1) for x in np.percentile(center[both] - tgt[both], [25, 50, 75])]

        def shift_at(t: float, shift=shift) -> float:
            k = int(round(t * FPS))
            if 0 <= k < len(shift) and shift[k] != 0:
                return float(shift[k])
            lo, hi = max(0, k - 5), min(len(shift), k + 6)
            nz = [q for q in range(lo, hi) if shift[q] != 0]
            return float(shift[min(nz, key=lambda q: abs(q - k))]) if nz else 0.0

        y = psola(seg, sr, shift_at, w.duration_points(a_in, b_in))
        f = min(len(y) // 4, int(0.01 * sr))
        if f > 1:
            y[:f] *= np.linspace(0, 1, f)
            y[-f:] *= np.linspace(1, 0, f)
        at = max(0, int(w.at(a_in) * sr))
        n = min(len(y), len(out) - at)
        out[at:at + n] += y[:n]
        rep.update(rows=list(tk.rows), take=tk.src, heard=round(tk.score, 2), octave=oct_here, src=round(tk.s, 2),
                   dst=round(w.o0, 2), ratio=round(w.ts[-1] / max(1e-6, w.taus[-1]), 3),
                   warp="dtw" if len(w.taus) > 2 else "ровно")
        report.append(rep)
        if trace is not None:   # замеры: кусок до и после, по строкам
            trace.append({"rows": tk.rows, "seg": seg, "out": y, "rep": rep})
    total = sum(r["locked"] + r["snapped"] for r in report)
    summary = {"tuning": round(tune, 3), "octave": octave, "takes": len(report),
               "locked": round(sum(r["locked"] for r in report) / max(1, total), 3), "lines": report}
    return out[:n_out], summary


def lock(voices: list[np.ndarray], row_texts: list[str], rows: list[tuple[float, float]], sr: int, f0o: np.ndarray,
         key: dict, ov: np.ndarray | None = None, stt_url: str = "http://127.0.0.1:8097",
         heard: list[list[tuple[str, float, float]]] | None = None, progress: C.Progress = C.no_progress,
         timing: str = "dtw", pitch: bool = True, trace: list | None = None) -> tuple[np.ndarray, list[tuple[float, float] | None], dict]:
    """Весь замок куска: где какой вариант что спел (whisper), лучший дубль на строку, время и ноты певицы.
    Возвращает голос, где звучит каждая строка (None — её не спел ни один вариант) и отчёт."""
    import re
    row_words = [re.findall(r"[а-яa-z]+", t.lower().replace("ё", "е")) for t in row_texts]
    if heard is None:
        heard = []
        for vi, v in enumerate(voices):
            progress(0.3 * vi / max(1, len(voices)), f"слушаю, где что спето: вариант {vi + 1}/{len(voices)}")
            heard.append(heard_words(v, sr, stt_url))
    takes = takes_by_words(voices, heard, row_words, rows, sr, f0o, ov)
    y, rep = render(voices, takes, sr, f0o, key, ov, lambda p, m: progress(0.3 + 0.7 * p, m), timing, pitch, trace)
    placed: list[tuple[float, float] | None] = [None] * len(rows)
    for tk in takes:
        placed[tk.rows[0]] = (tk.o0, tk.o1)
    rep["rows"] = len(rows)
    rep["missed_rows"] = [i for i, p in enumerate(placed) if p is None]
    return y, placed, rep


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("track")
    ap.add_argument("out")
    ap.add_argument("vocals", nargs="+", help="голоса вариантов ACE на времени песни (или куска --from/--to)")
    ap.add_argument("--from", dest="t0", type=float, default=0.0)
    ap.add_argument("--to", dest="t1", type=float, default=0.0)
    ap.add_argument("--lyrics", required=True, help="русский текст (NN | строка): строки без текста и «=» — не строки для голоса")
    ap.add_argument("--timing", default="dtw", choices=["dtw", "flat", "shift", "none"],
                    help="dtw — слоги на ноты; flat — ровная растяжка; shift — только сдвиг к строке; none — на месте")
    ap.add_argument("--no-pitch", action="store_true", help="тон не трогать (замер)")
    ap.add_argument("--part", action="store_true", help="голоса уже на времени куска (варианты ACE из parts), не резать")
    a = ap.parse_args()
    d = Path(a.track)
    an = json.loads((d / "analysis.json").read_text(encoding="utf-8"))
    plan = json.loads((d / "plan.json").read_text(encoding="utf-8"))
    f0o = np.load(d / an["vocal_f0"])
    voices = [C.mono(C.read(Path(p))) for p in a.vocals]
    t1 = a.t1 or (a.t0 + max(len(v) for v in voices) / C.SR if a.part else max(len(v) for v in voices) / C.SR)
    if not a.part:
        voices = [v[int(a.t0 * C.SR):int(t1 * C.SR)] for v in voices]
    ov = C.mono(C.read(d / "vocals.flac"))[int(a.t0 * C.SR):int(t1 * C.SR)]
    ru = CV.read_lyrics(Path(a.lyrics), len(plan["lines"]))
    keep = [(l, r) for l, r in zip(plan["lines"], ru) if a.t0 <= float(l["t0"]) < t1 and r is not None and r.strip() not in ("", "=")]
    rows = [(float(l["t0"]) - a.t0, float(l["t1"]) - a.t0) for l, _ in keep]
    y, placed, rep = lock(voices, [r for _, r in keep], rows, C.SR, f0o[int(a.t0 * FPS):int(t1 * FPS)], an.get("key", {}), ov,
                          progress=lambda p, m: print(f"{int(p * 100):3d}% {m}", flush=True), timing=a.timing,
                          pitch=not a.no_pitch)
    C.write(Path(a.out), y)
    print(json.dumps({k: v for k, v in rep.items() if k != "lines"}, ensure_ascii=False))
    for r in rep["lines"]:
        print(r)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
