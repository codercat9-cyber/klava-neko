"""Русский кавер живым вокалом: ACE-Step 1.5 поёт песню с новым текстом по смысловым кодам оригинала (мелодия, тайминг),
кусками до ~110 с по паузам между строфами; на каждый кусок — несколько дублей (turbo, turbo от зашумлённого
оригинала, sft); из дублей сводится лучший по строкам и ставится на время и ноты певицы (tune.py), Seed-VC переводит
голос в тембр Клавы, сведение — с настоящим минусом песни.

    tts\\.venv\\Scripts\\python.exe music\\ace_cover.py <трек> <русский текст: NN | строка> [--seeds 7,11,23] [--keep 0.3]
                                                   [--caption "стиль по-английски"] [--tag вариант] [--no-lock]

Замеры 30.09 на «Lately»: прежний путь (речь Клавы → ноты через WORLD) друг забраковал — «глючно, не мелодично,
отрывками». Голос ACE на 100 с: 0,14–0,29 ошибок по словам, 52–57 % кадров в полутоне от мелодии оригинала; вся песня
одним куском — сдвиг на тритон и 22 %. После Seed-VC: keep 0 — тембр к эталону Клавы 0,956 при 0,34 ошибок, keep 0,2 —
0,95 / 0,27, keep 0,3 — 0,93 / 0,16 (шкала: сама Лера Линн — 0,79, чужие женские голоса — 0,77–0,80) — берём 0,3:
друг жаловался именно на невнятность.

30.09 вечер, друг: «сделай мелодично, чтобы в ноты попадала и под мелодию». Замер по строкам: ACE поёт мимо нот певицы
(36 % кадров в полутоне, со второй половины — на 3,5–5,5 полутона в свою линию), строки — на 1–2 с раньше минуса,
повторяет и пропускает их. Сведение дублей по строкам + замок на её ноты и время (голос ACE v4, без Seed-VC): в
полутоне 0,35 → 0,73, покрытие её фраз 0,52 → 0,66, ошибок по словам 0,28 → 0,19.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import analyze as A  # noqa: E402
import common as C  # noqa: E402
import cover as CV  # noqa: E402
import tune as T  # noqa: E402

ACE = C.ROOT / "tools" / "ace-step"
ACE_PY = ACE / ".venv" / "Scripts" / "python.exe"
KEEP = 0.3
SEEDS = (7, 11, 23)        # turbo с чистого шума
# turbo от зашумлённого оригинала (cover_noise_strength: 0 — чистый шум, 1 — сам оригинал с английскими словами).
# Замер 30.09 вечером, «Lately»: в полутоне от нот певицы 0,80–0,91 и в её регистре 0,88–0,99 против 0,1–0,6 у дублей
# с чистого шума; слова — от 0,39 ошибок до «не разобрать», поэтому дублей два уровня шума. v6 (перевод друга): 0,3 —
# лучший дубль проекта (0,155 ошибок, 89 % в нотах, 98 % в регистре); 0,45 — слов уже нет (0,86–1,0), сквозит оригинал
NOISE_RUNS = ((0.3, (61, 67)), (0.22, (71, 73)))
# sft (50 шагов, CFG 7) в режиме кавера не пела вовсе — whisper слышал «музыка музыка…» во всех 6 дублях: не берём
EXTRA_SEEDS = (31, 47)     # второй круг куску, где строку внятно не спел ни один дубль
JUDGE_V = 3
MAX_PART = 110.0      # сек: длиннее ACE теряет мелодию и тональность
STANZA_GAP = 6.0      # сек: пауза между строфами (проигрыш)


def available() -> bool:
    return ACE_PY.exists() and (ACE / "checkpoints" / "acestep-v15-turbo").is_dir()


def runs_for(seeds: list[int], extra: bool = False) -> list[dict]:
    """Какие дубли петь: turbo с чистого шума и от зашумлённого оригинала двух уровней; второй круг — ещё по дублю обоих."""
    runs = [{"model": "acestep-v15-turbo", "seeds": list(seeds)}]
    if extra:
        runs[0]["seeds"] = list(seeds[:1])
        runs.append({"model": "acestep-v15-turbo", "seeds": list(seeds[1:]), "noise": NOISE_RUNS[0][0]})
        return runs
    runs += [{"model": "acestep-v15-turbo", "seeds": list(sd), "noise": n} for n, sd in NOISE_RUNS]
    return runs


def voice_hint(plan: dict) -> str:
    """Регистр голоса по певице: ACE сам уходил на 7–9 полутонов выше Леры (альт) — тянуть назад PSOLA-й плохо."""
    m = float(plan.get("median_midi") or 0)
    if not m:
        return "soft, gentle, melodic female vocal"
    return ("soft, low, warm alto female vocal" if m < 62 else "soft, warm mezzo-soprano female vocal" if m < 67
            else "soft, clear soprano female vocal")


def key_text(an: dict) -> str:
    k = an.get("key") or {}
    return f"{k['tonic']} {'minor' if k.get('mode') == 'minor' else 'major'}" if k.get("tonic") else ""


def words(s: str) -> list[str]:
    import re
    return re.findall(r"[а-яёa-z]+", s.lower().replace("ё", "е"))


def wer(ref: list[str], hyp: list[str]) -> float:
    d = np.zeros((len(ref) + 1, len(hyp) + 1))
    d[:, 0] = np.arange(len(ref) + 1)
    d[0, :] = np.arange(len(hyp) + 1)
    for i in range(1, len(ref) + 1):
        for j in range(1, len(hyp) + 1):
            d[i, j] = min(d[i - 1, j] + 1, d[i, j - 1] + 1, d[i - 1, j - 1] + (ref[i - 1] != hyp[j - 1]))
    return float(d[-1, -1] / max(1, len(ref)))


# ---------- куски песни ----------

def blocks_of(lines: list[dict], lyrics: list[str | None]) -> list[dict]:
    """Строфы по паузам ≥ STANZA_GAP с метками: строфа, чьи строки повторяются в других, — [Chorus], иначе [Verse n].
    Строфа длиннее куска режется по самой длинной паузе между строками."""
    rows = [(float(l["t0"]), float(l["t1"]), r.strip()) for l, r in zip(lines, lyrics)
            if r is not None and r.strip() and r.strip() != "="]
    if not rows:
        raise ValueError("нет русского текста для кавера")
    stanzas: list[list[tuple[float, float, str]]] = [[rows[0]]]
    for prev, cur in zip(rows, rows[1:]):
        if cur[0] - prev[1] >= STANZA_GAP:
            stanzas.append([cur])
        else:
            stanzas[-1].append(cur)
    keys = [{" ".join(words(r[2])) for r in st} for st in stanzas]
    out: list[dict] = []
    verse = 0
    for i, st in enumerate(stanzas):
        others = set().union(*(k for j, k in enumerate(keys) if j != i)) if len(keys) > 1 else set()
        chorus = sum(1 for r in st if " ".join(words(r[2])) in others) / len(st) >= 0.5
        if not chorus:
            verse += 1
        label = "[Chorus]" if chorus else f"[Verse {verse}]"
        pending = [st]
        while pending:
            s = pending.pop(0)
            if s[-1][1] - s[0][0] > MAX_PART - 12 and len(s) > 1:
                cut = max(range(1, len(s)), key=lambda j: s[j][0] - s[j - 1][1])
                pending[:0] = [s[:cut], s[cut:]]
                continue
            out.append({"t0": s[0][0], "t1": s[-1][1], "label": label, "rows": s})
    return out


def parts_of(blocks: list[dict], duration: float) -> list[dict]:
    """Куски до MAX_PART: границы — в середине проигрышей между строфами, жадно сколько влезет."""
    n = len(blocks)
    cuts = [(blocks[i]["t1"] + blocks[i + 1]["t0"]) / 2 for i in range(n - 1)] + [duration]
    parts: list[dict] = []
    start, cur = 0.0, []
    for i in range(n):
        if cur and cuts[i] - start > MAX_PART:
            parts.append({"t0": start, "t1": cuts[i - 1], "blocks": cur})
            start, cur = cuts[i - 1], []
        cur.append(blocks[i])
    parts.append({"t0": start, "t1": duration, "blocks": cur})
    last = len(parts) - 1
    for k, p in enumerate(parts):
        text: list[str] = []
        if p["blocks"][0]["t0"] - p["t0"] > 4.0:
            text += ["[Intro]" if k == 0 else "[Instrumental]", ""]
        for j, b in enumerate(p["blocks"]):
            if j > 0:
                text += ["[Instrumental]", ""]
            text.append(b["label"])
            text += [r[2] for r in b["rows"]]
            text.append("")
        if p["t1"] - p["blocks"][-1]["t1"] > 4.0:
            text.append("[Outro]" if k == last else "[Instrumental]")
        p["name"] = f"p{k}"
        p["lyrics"] = "\n".join(text).strip() + "\n"
        p["words"] = words(" ".join(r[2] for b in p["blocks"] for r in b["rows"]))
    return parts


def fallback_caption(an: dict) -> str:
    """Стиль по меткам слуха (AST), если ни мозг, ни LM ACE не описали."""
    lab = an.get("labels", {})
    genre = [r[0] for r in lab.get("genre", [])[:2] if r[2] >= 0.02]
    inst = [r[0].lower() for r in lab.get("instruments", [])[:4] if r[2] >= 0.03]
    mood = [r[0].replace(" music", "").lower() for r in lab.get("mood", [])[:1] if r[2] >= 0.05]
    return ", ".join(mood + [g.replace(" music", "").lower() for g in genre] + inst) or "emotional song"


# ---------- ACE отдельным процессом ----------

def generate(parts: list[dict], full: Path, out: Path, runs: list[dict], caption: str, fallback: str, vocal: str,
             keyscale: str, progress: C.Progress, p0: float, p1: float) -> tuple[dict[str, list[tuple[int, Path]]], str]:
    """ACE-Step своим окружением: дубли каждого куска (runs — модели и сиды) и итоговое описание стиля."""
    out = out.resolve()   # процесс ACE живёт в своей папке — относительные пути там не те
    out.mkdir(parents=True, exist_ok=True)
    seeds = [s for r in runs for s in r["seeds"]]
    req = {"parts": [{"name": p["name"], "src": str(Path(p["src"]).resolve()), "lyrics": p["lyrics"]} for p in parts],
           "full": str(full.resolve()), "language": "ru", "runs": runs, "strength": 1.0, "keyscale": keyscale,
           "out": str(out), "caption": caption, "fallback_caption": fallback, "vocal": vocal}
    rq = out / "request.json"
    rq.write_text(json.dumps(req, ensure_ascii=False, indent=1), encoding="utf-8")

    def run_once() -> tuple[dict[str, list[tuple[int, Path]]], str, str, list[str], int | None]:
        proc = subprocess.Popen([str(ACE_PY), str(Path(__file__).resolve().parent / "ace_gen.py"), str(rq)],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                env={**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONUNBUFFERED": "1"},
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        got: dict[str, list[tuple[int, Path]]] = {}
        used, err, tail = "", "", []
        try:
            assert proc.stdout is not None
            for line in proc.stdout:
                line = line.rstrip()
                tail = (tail + [line])[-12:]
                if line.startswith("P "):
                    bits = line.split(" ", 2)
                    try:
                        progress(p0 + (p1 - p0) * float(bits[1]), "ACE: " + (bits[2] if len(bits) > 2 else ""))
                    except ValueError:
                        pass
                elif line.startswith("C "):
                    used = line[2:]
                    progress(p0 + (p1 - p0) * 0.14, f"ACE: стиль — {used[:120]}")
                elif line.startswith("W "):
                    name, seed, path = line[2:].split(" ", 2)
                    got.setdefault(name, []).append((int(seed), Path(path)))
                elif line.startswith("E "):
                    err = line[2:]
            proc.wait()
        finally:
            if proc.poll() is None:
                proc.kill()
        return got, used, err, tail, proc.returncode

    # процесс ACE изредка молча умирает посреди (замер 30.09: на 5-м прогоне из 9, выгрузка модели в ОЗУ) — спетое
    # остаётся на диске, заходим ещё раз: ace_gen.py пропускает готовые варианты и допевает недостающие
    got: dict[str, list[tuple[int, Path]]] = {}
    used, err, tail = "", "", []
    # заходов — по объёму: падает примерно раз в 7–8 дублей (замер 30.09 вечером: 21 дубль, вылет на 8-м)
    tries = 3 + len(parts) * len(seeds) // 7
    for attempt in range(tries):
        got, used, err, tail, code = run_once()
        if err or all(len(got.get(p["name"], [])) >= len(seeds) for p in parts):
            break
        C.log(f"ACE: процесс вышел с кодом {code}, не допев ({sum(len(v) for v in got.values())} из {len(parts) * len(seeds)}) — "
              f"захожу ещё раз ({attempt + 2}/{tries})")
        progress(p0 + (p1 - p0) * 0.5, "ACE споткнулся посреди — допеваю недостающее…")
    (out / "ace_gen.log").write_text("\n".join(tail), encoding="utf-8")
    missing = [p["name"] for p in parts if not got.get(p["name"])]
    if missing:
        raise RuntimeError(err or f"ACE-Step не спел куски {', '.join(missing)}: " + " | ".join(tail[-4:]))
    return got, used


def judge(wav: Path, ref_words: list[str], f0o: np.ndarray, stt_url: str) -> dict:
    """Разложить дубль на голос и минус; слова с временем (whisper — для сведения по строкам), ошибки по словам, доля
    кадров в полутоне от нот певицы (октава не в счёт, сдвиг ±0,1 с) и в пределах трёх полутонов без сворачивания
    октав (near: чем ближе регистр к певице, тем меньше правит замок). Замер 30.09: «сдвиг на тритон» у вариантов — не
    транспозиция, а своя мелодическая линия; shift — только для журнала."""
    cache = wav.parent / "judge.json"
    if cache.exists() and (wav.parent / "ace_vocals.wav").exists():   # перезапуск кавера — оценка уже есть
        old = json.loads(cache.read_text(encoding="utf-8"))
        if old.get("v") == JUDGE_V:
            return old
    stems = A.separate(C.read(wav), C.no_progress)
    v = stems["vocals"]
    C.write(wav.parent / "ace_vocals.wav", v)
    vm = C.mono(v)
    hw = T.heard_words(vm, C.SR, stt_url)
    w = wer(ref_words, [x[0] for x in hw])
    f0a = A.vocal_f0(vm, C.SR)
    n = min(len(f0o), len(f0a))
    best, shift, near = 0.0, 0, 0.0
    for lag in range(-10, 11, 2):
        a = np.roll(f0a[:n], -lag)
        both = (f0o[:n] > 0) & (a > 0)
        if both.sum() < 100:
            continue
        c = 1200 * np.log2(a[both] / f0o[:n][both])
        within = float(np.mean(np.abs((c + 600) % 1200 - 600) < 100))
        if within > best:
            k = int(np.argmax(np.bincount(np.round(c / 100).astype(int) % 12, minlength=12)))
            best, shift, near = within, (k if k <= 6 else k - 12), float(np.mean(np.abs(c) < 300))
    score = (1 - w) + 0.3 * best + 0.3 * near
    res = {"v": JUDGE_V, "wer": round(w, 3), "melody": round(best, 3), "near": round(near, 3), "shift": shift,
           "score": round(score, 3), "words": [[x[0], round(x[1], 2), round(x[2], 2)] for x in hw]}
    cache.write_text(json.dumps(res), encoding="utf-8")
    return res


def weak_rows(p: dict, scores: list[dict]) -> list[int]:
    """Строки куска, которые внятно (≥ 60 % слов) не спел ни один дубль."""
    import re
    rows = [r for blk in p["blocks"] for r in blk["rows"]]
    row_words = [re.findall(r"[а-яa-z]+", r[2].lower().replace("ё", "е")) for r in rows]
    best = [0.0] * len(rows)
    for s in scores:
        for i, f in enumerate(T.align_rows([tuple(x) for x in s.get("words", [])], row_words)):
            if f is not None:
                best[i] = max(best[i], f[0])
    return [i for i, b in enumerate(best) if b < 0.6]


def heard(y: np.ndarray, sr: int, stt_url: str) -> list[str]:
    r = A.whisper_call(C.resample(y, sr, 16000), stt_url, "ru")
    return words(" ".join(s.get("text", "") for s in r.get("segments", [])))


def best_of(singer: CV.Singer, seg: np.ndarray, stt_url: str, tries: int = 3) -> tuple[np.ndarray, int]:
    """Перекраска куска в тембр Клавы — лучшая из нескольких: диффузия Seed-VC случайна, и слова то остаются, то
    смазываются (замер 30.09, вся «Lately»: голос ACE 0,28 ошибок по словам → после перекраски 0,38–0,50). Образец —
    что whisper слышит в самом куске ACE; берём вариант, где у Клавы слышно то же. Распев без слов — первый же."""
    target = heard(seg, singer.sr, stt_url)
    best, best_e, n = None, 9.0, 0
    for n in range(1, tries + 1):
        y = singer(seg)[: len(seg)]
        if len(target) < 2:
            return y, n
        e = wer(target, heard(y, singer.sr, stt_url))
        if e < best_e:
            best, best_e = y, e
        if e <= 0.15:
            break
    return best, n


# ---------- весь кавер ----------

def sing_ace(track: str | Path, lyrics: list[str | None], progress: C.Progress = C.no_progress,
             stt_url: str = "http://127.0.0.1:8097", seeds: list[int] | None = None, keep: float = KEEP, tag: str = "",
             caption: str = "", locked: bool = True) -> dict:
    """caption — стиль песни по-английски (жанр, настроение, темп, инструменты; без вокала), пусто — опишет LM ACE.
    locked — сведение дублей по строкам и замок на ноты и время певицы (False — лучший дубль куска как есть)."""
    t_start = time.time()
    if not available():
        raise RuntimeError("ACE-Step не установлен (tools\\setup_full.ps1 → шаг ACE-Step)")
    d = C.track_dir(track) if Path(track).is_file() else Path(track)
    pj = d / "plan.json"
    plan = json.loads(pj.read_text(encoding="utf-8")) if pj.exists() else CV.make_plan(track, progress)
    an = json.loads((d / "analysis.json").read_text(encoding="utf-8"))
    lines = plan["lines"]
    lyrics = (list(lyrics) + [None] * len(lines))[: len(lines)]
    mix = C.read(d / "mix.flac")
    duration = len(mix) / C.SR
    parts = parts_of(blocks_of(lines, lyrics), duration)
    out = CV.cover_dir(plan, "ru-ace" + (f"-{C.slug(tag)}" if tag else ""))
    (out / "lyrics_ace.txt").write_text("\n".join(f"# {p['name']}: {p['t0']:.1f}–{p['t1']:.1f} с\n{p['lyrics']}" for p in parts),
                                        encoding="utf-8")
    pdir = out / "parts"
    pdir.mkdir(exist_ok=True)
    for p in parts:
        p["src"] = str(C.write(pdir / f"{p['name']}.wav", mix[int(p["t0"] * C.SR):int(p["t1"] * C.SR)]))
    runs = runs_for(list(seeds or SEEDS))
    model_of = {sd: r["model"].split("-")[-1] + (f"+шум {r['noise']}" if r.get("noise") else "")
                for r in runs + runs_for(list(EXTRA_SEEDS), extra=True) for sd in r["seeds"]}
    vocal_desc = voice_hint(plan)
    progress(0.02, f"ACE-Step: пою живым вокалом по-русски, кусков {len(parts)}, дублей на кусок "
                   f"{sum(len(r['seeds']) for r in runs)}…")
    got, used = generate(parts, d / "mix.flac", out / "ace", runs, caption, fallback_caption(an), vocal_desc, key_text(an),
                         progress, 0.02, 0.5)
    f0o = np.load(d / an["vocal_f0"])
    vocal = np.zeros(len(mix), np.float32)
    report = []
    timed: list[tuple[float, float, str]] = []
    total = sum(len(v) for v in got.values())
    k = 0

    def judged(p: dict, variants: list[tuple[int, Path]]) -> list[dict]:
        nonlocal k
        out_ = []
        for seed, wav in variants:
            progress(0.5 + 0.2 * min(1.0, k / total), f"слушаю кусок {p['name']}, вариант {k + 1}: слова и мелодия…")
            k += 1
            s = dict(judge(wav, p["words"], f0o[int(p["t0"] * 100):int(p["t1"] * 100)], stt_url))
            s.update(seed=seed, path=str(wav), model=model_of.get(seed, "turbo"))
            out_.append(s)
            C.log(f"ACE {p['name']} сид {seed} ({s['model']}): ошибок по словам {s['wer']}, в нотах оригинала {s['melody']}, "
                  f"в регистре певицы {s['near']}, итог {s['score']}")
        return out_

    all_scores = {p["name"]: judged(p, got[p["name"]]) for p in parts}
    # кусок, где строку внятно не спел ни один дубль (протяжный припев, распев), — ещё два сида
    weak = [p for p in parts if weak_rows(p, all_scores[p["name"]])]
    if weak:
        progress(0.62, f"ACE: допеваю невнятные строки ({', '.join(p['name'] for p in weak)})…")
        more, _ = generate(weak, d / "mix.flac", out / "ace", runs_for(list(EXTRA_SEEDS), extra=True), used,
                           fallback_caption(an), vocal_desc, key_text(an), progress, 0.62, 0.66)
        for p in weak:
            all_scores[p["name"]] += judged(p, [x for x in more[p["name"]] if x[0] in EXTRA_SEEDS])
    ov_full = C.mono(C.read(d / "vocals.flac"))
    for pi, p in enumerate(parts):
        a, b = int(p["t0"] * C.SR), int(p["t1"] * C.SR)
        scores = all_scores[p["name"]]
        best = max(scores, key=lambda s: s["score"])
        rows = [r for blk in p["blocks"] for r in blk["rows"]]
        part_rep = {"part": p["name"], "from": round(p["t0"], 1), "to": round(p["t1"], 1), "chosen": best["seed"],
                    "variants": [{kk: vv for kk, vv in s.items() if kk not in ("path", "words")} for s in scores]}
        if locked:
            # сведение дублей по строкам и замок на время и ноты певицы (tune.py)
            voices = [C.mono(C.read(Path(s["path"]).parent / "ace_vocals.wav"))[: b - a] for s in scores]
            v, placed, lk = T.lock(voices, [r[2] for r in rows], [(r[0] - p["t0"], r[1] - p["t0"]) for r in rows], C.SR,
                                   f0o[int(p["t0"] * 100):int(p["t1"] * 100) + 1], an.get("key", {}), ov_full[a:b],
                                   heard=[[tuple(x) for x in s["words"]] for s in scores],
                                   progress=lambda q, m, pi=pi: progress(0.66 + 0.06 * (pi + q) / len(parts), m))
            timed += [(p["t0"] + pl[0], p["t0"] + pl[1], r[2]) for pl, r in zip(placed, rows) if pl is not None]
            for ln in lk["lines"]:
                ln["take"] = scores[ln["take"]]["seed"]
            part_rep["lock"] = lk
            C.log(f"замок {p['name']}: строк {len(rows) - len(lk['missed_rows'])}/{len(rows)}, в нотах {lk['locked']}, "
                  f"дубли по строкам: {[ln['take'] for ln in lk['lines']]}")
        else:
            v = C.mono(C.read(Path(best["path"]).parent / "ace_vocals.wav"))[: b - a]
            timed += [(r[0], r[1], r[2]) for r in rows]
        fade = int(0.3 * C.SR)
        v = v.copy()
        if a > 0 and len(v) > 2 * fade:
            v[:fade] *= np.linspace(0, 1, fade)
        if b < len(mix) and len(v) > 2 * fade:
            v[-fade:] *= np.linspace(1, 0, fade)
        vocal[a:a + len(v)] += v
        report.append(part_rep)
    timed.sort(key=lambda x: x[0])
    C.free_gpu()
    C.write(out / "ace_vocal.wav", vocal)
    progress(0.72, "Seed-VC: перекрашиваю в мой голос…")
    singer = CV.Singer(steps=30, keep=keep)
    x = C.resample(vocal, C.SR, singer.sr) if singer.sr != C.SR else vocal
    klava = np.zeros_like(x)
    chunks = CV.voice_chunks(x, singer.sr)
    tries = []
    for ci, (a, b) in enumerate(chunks):
        progress(0.72 + 0.2 * ci / max(1, len(chunks)), f"пою кусок {ci + 1}/{len(chunks)}")
        y, n = best_of(singer, x[a:b], stt_url)
        tries.append(n)
        f = min(len(y) // 4, int(0.01 * singer.sr))
        if f > 1:
            y[:f] *= np.linspace(0, 1, f)
            y[-f:] *= np.linspace(1, 0, f)
        klava[a:b] += y
    klava = C.resample(klava, singer.sr, C.SR) if singer.sr != C.SR else klava
    del singer
    C.free_gpu()
    C.write(out / "klava_raw.wav", klava)
    progress(0.93, "свожу с минусом оригинала…")
    inst = sum(C.read(d / f"{s}.flac") for s in ("drums", "bass", "other"))
    # громкость и её ход — по голосу ACE, а не певицы: ACE поёт те же строки плотнее (куплет за 25 с вместо 32), и
    # огибающая оригинала глушила Клаву там, где певица молчала (замер 30.09: 0,28 ошибок по словам у ACE → 0,57 в итоге)
    # а уровень — как у певицы в оригинале: у ACE свой баланс, голос выходил на 6,5 дБ тише минуса против 1,1 у Леры
    ov = C.read(d / "vocals.flac")
    lv_ace = C.loudness(np.stack([vocal, vocal], axis=1), C.SR)[0]
    lv_orig = C.loudness(ov if ov.ndim > 1 else np.stack([ov, ov], axis=1), C.SR)[0]
    ref = vocal * 10 ** ((lv_orig - lv_ace) / 20) if lv_ace > -69 and lv_orig > -69 else vocal
    cover, vproc = CV.master(klava, ref, inst, mix, C.SR)
    C.write(out / "cover.wav", cover)
    C.write(out / "vocal.wav", vproc)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(out / "cover.wav"), "-codec:a", "libmp3lame", "-b:a", "192k",
                    str(out / "cover.mp3")], check=False, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    subs = [{"t0": round(t0, 2), "t1": round(t1, 2), "text": t} for t0, t1, t in timed]
    info = {"title": plan["title"], "artist": plan.get("artist", ""), "mode": "ru", "engine": "ace", "dir": str(out),
            "cover": "cover.wav", "mp3": "cover.mp3", "vocal": "vocal.wav", "duration": round(len(cover) / C.SR, 2),
            "beats": an.get("beats", []), "downbeats": an.get("downbeats", []), "lines": subs, "shift": 0,
            "report": {"caption": used, "keep": keep, "locked": locked, "parts": report, "vc_tries": tries},
            "mood": [r[0] for r in an.get("labels", {}).get("mood", [])[:2]], "seconds": round(time.time() - t_start, 1)}
    C.save_json(out / "cover.json", info)
    progress(1.0, f"кавер готов за {info['seconds']:.0f} с: {out / 'cover.mp3'}")
    return info


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("track")
    ap.add_argument("lyrics", help="файл с русскими строками (NN | текст) по фразам плана")
    ap.add_argument("--seeds", default=",".join(map(str, SEEDS)))
    ap.add_argument("--keep", type=float, default=KEEP)
    ap.add_argument("--tag", default="")
    ap.add_argument("--caption", default="", help="стиль по-английски; пусто — опишет LM ACE по звуку")
    ap.add_argument("--no-lock", action="store_true", help="без сведения дублей и замка на ноты (как до 30.09 вечера)")
    a = ap.parse_args()
    d = C.track_dir(a.track) if Path(a.track).is_file() else Path(a.track)
    plan = json.loads((d / "plan.json").read_text(encoding="utf-8"))
    ru = CV.read_lyrics(Path(a.lyrics), len(plan["lines"]))
    info = sing_ace(a.track, ru, progress=lambda p, m: print(f"{int(p * 100):3d}% {m}", flush=True),
                    seeds=[int(s) for s in a.seeds.split(",") if s.strip()], keep=a.keep, tag=a.tag, caption=a.caption,
                    locked=not a.no_lock)
    print(json.dumps({k: v for k, v in info.items() if k not in ("beats", "downbeats", "lines")}, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
