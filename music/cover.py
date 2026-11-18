"""Клава поёт кавер своим голосом — по-русски (эквиритмический перевод) или в оригинале.

Друг 30.09: «английскую песню скормить — она обработает и красиво споёт на русском». Обычный перенос голоса (RVC,
Seed-VC) оставляет английские слова и акцент, а русская речь из TTS не попадает в ноты. Поэтому пение собирается:

  1. разбор трека (analyze.py): чистый голос, минус, тон голоса по 10 мс, слова whisper с таймкодами;
  2. фразы — по дыханию (паузам тона), в каждой — слоги оригинала: ноты из тона, подогнанные к числу английских
     слогов (мелизмы склеиваются по самым слабым границам, длинные ноты делятся); сильные — длинные и на сильной доле;
  3. русский текст — строго столько же слогов в строке (переводит мозг Клавы по листу translate.txt, проверка слогов
     и ударений — здесь же), строка «=» — спеть оригинал её тембром, пустая — промолчать;
  4. направляющий голос: строка читается голосом Клавы (Qwen3-TTS), фонемы выравниваются (wav2vec2 espeak, CTC по
     множествам фонем), WORLD растягивает гласные на ноты, согласные ставит ПЕРЕД долей (так поют), тон — рисунок
     оригинала (с вибрато и подъездами), сдвинутый на целые октавы, чтобы не разойтись с минусом по тональности;
  5. Seed-VC (DiT с тоном, 44 кГц) перекрашивает направляющий голос в живое пение её тембром, тон не трогает;
  6. громкость — как у оригинального голоса по фразам, компрессор, реверберация, сведение с минусом, лимитер.

    tts\\.venv\\Scripts\\python.exe music\\cover.py plan <трек>
    tts\\.venv\\Scripts\\python.exe music\\cover.py sing <трек> [--lyrics файл] [--orig]
"""
from __future__ import annotations

import argparse
import io
import json
import re
import sys
import time
import urllib.request
from pathlib import Path
from types import SimpleNamespace

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import analyze as A  # noqa: E402
import common as C  # noqa: E402

TTS_URL = "http://127.0.0.1:8095"
REF = C.ROOT / "tts" / "voices" / "candidates" / "klava_02.wav"
RU_V = "аеёиоуыэюя"
FPS = 100                 # тон оригинала (RMVPE) — кадры по 10 мс
WFP = 5.0                 # кадр WORLD, мс
KLAVA_CENTER = 64         # середина её певческого диапазона (E4): оригинал сдвигается к ней на целые октавы


# ---------- слоги ----------

EN_EXC = {"the": 1, "every": 2, "everything": 3, "evening": 2, "fire": 1, "hour": 1, "our": 1, "flower": 2, "heaven": 2,
          "being": 2, "going": 2, "doing": 2, "saying": 2, "lying": 2, "dying": 2, "trying": 2, "crying": 2, "flying": 2,
          "business": 2, "people": 2, "little": 2, "something": 2, "somewhere": 2, "someone": 2, "maybe": 2, "baby": 2,
          "lately": 2, "tired": 1, "wanna": 2, "gonna": 2, "gotta": 2, "i'm": 1, "you're": 1, "we're": 1, "they're": 1,
          "i've": 1, "i'll": 1, "you'll": 1, "i'd": 1, "there's": 1, "it's": 1, "don't": 1, "can't": 1, "won't": 1,
          "ain't": 1, "love": 1, "loved": 1, "alive": 2, "idea": 3, "real": 1, "really": 2, "cruel": 2, "poem": 2,
          "quiet": 2, "science": 2, "violent": 3, "diamond": 2, "different": 2, "family": 3, "chocolate": 2, "vanished": 2}


def en_syllables(word: str) -> int:
    w = re.sub(r"[^a-z']", "", word.lower())
    if not w:
        return 0
    if w in EN_EXC:
        return EN_EXC[w]
    w = w.replace("'", "")
    n = len(re.findall(r"[aeiouy]+", w))
    if w.endswith("e") and not w.endswith(("le", "ee", "ye")) and n > 1:
        n -= 1
    if w.endswith("ed") and n > 1 and not w.endswith(("ted", "ded")):
        n -= 1
    if w.endswith("es") and n > 1 and not w.endswith(("ses", "zes", "ches", "shes", "ges", "ces", "xes")):
        n -= 1
    return max(1, n)


def ru_syllables(text: str) -> int:
    return sum(1 for ch in text.lower() if ch in RU_V)


# ---------- фразы и слоги оригинала ----------

def runs(mask: np.ndarray) -> list[list[int]]:
    out: list[list[int]] = []
    on = False
    for i, v in enumerate(mask):
        if v and not on:
            out.append([i, i + 1])
            on = True
        elif v:
            out[-1][1] = i + 1
        else:
            on = False
    return out


def phrases_of(f0: np.ndarray, words: list[list], lines: list[list]) -> list[dict]:
    """Фразы = строки whisper (он режет песню по строкам текста верно, а вот таймкоды слов на пении бывают слипшимися);
    границы уточняются по тону: начало ищется до секунды раньше строки, конец — до следующей строки.
    Голос вне строк (распев, «о-о-о», пропущенное whisper) — отдельные фразы без слов."""
    voiced = f0 > 0
    n = len(f0)
    out: list[dict] = []
    wi = 0
    for li, (l0, l1, text) in enumerate(lines):
        # слово — ровно одной строке: строка whisper = её слова подряд (analyze.lyrics_of), делим по числу слов,
        # а не по времени — на стыке строк таймкоды слов у whisper слипаются
        cnt = len(text.split())
        ws = words[wi:wi + cnt]
        wi += cnt
        prev_end = out[-1]["t1"] + 0.05 if out else 0.0
        nxt = lines[li + 1][0] if li + 1 < len(lines) else n / FPS
        lo = int(max(prev_end, l0 - 1.0) * FPS)
        hi = int(min(nxt - 0.05, l1 + 0.8) * FPS)
        idx = np.flatnonzero(voiced[lo:hi])
        if idx.size == 0:
            continue
        # начало — начало звучащего куска, в котором (или сразу после которого) стоит первое слово
        a_ = lo + int(idx[0])
        for r0, r1 in runs(voiced[lo:hi]):
            if lo + r1 >= int((l0 - 0.1) * FPS):
                a_ = lo + r0
                break
        b_ = lo + int(idx[-1]) + 1
        out.append({"t0": a_ / FPS, "t1": b_ / FPS, "words": ws})
    # хвост в слог-два сразу после строки («…lock me / down») — её продолжение
    joined: list[dict] = []
    for p in out:
        k = sum(en_syllables(w[2]) for w in p["words"])
        if joined and 0 < k <= 2 and p["t0"] - joined[-1]["t1"] < 1.2 and joined[-1]["words"]:
            joined[-1]["t1"] = p["t1"]
            joined[-1]["words"].extend(p["words"])
            continue
        joined.append(p)
    # длинная строка (больше 14 слогов) — две фразы по самому длинному вдоху ближе к середине
    split: list[dict] = []
    for p in joined:
        k = sum(en_syllables(w[2]) for w in p["words"])
        a_, b_ = int(p["t0"] * FPS), int(p["t1"] * FPS)
        rr = runs(voiced[a_:b_])
        gaps = [(rr[j + 1][0] - rr[j][1], rr[j][1]) for j in range(len(rr) - 1)]
        mid = (b_ - a_) / 2
        big = [(g / (1 + abs(pos - mid) / max(1, mid)), pos) for g, pos in gaps if g >= 0.15 * FPS]
        if k > 14 and big:
            _, cut = max(big)
            tc = (a_ + cut) / FPS
            # слова — по числу спетых нот в каждой половине (таймкоды слов внутри длинной строки у whisper плывут),
            # при равенстве — ближе к доле времени
            frac = (tc - p["t0"]) / max(1e-6, p["t1"] - p["t0"])
            n1 = len(A.notes_from_f0(f0[a_:int(tc * FPS)]))
            n2 = len(A.notes_from_f0(f0[int(tc * FPS):b_]))
            syl = np.cumsum([en_syllables(w[2]) for w in p["words"]])
            j = int(np.argmin([abs(syl[i] - n1) + abs(k - syl[i] - n2) + abs(syl[i] / k - frac)
                               for i in range(len(syl) - 1)])) + 1 if len(syl) > 1 else 1
            w1, w2 = p["words"][:j], p["words"][j:]
            if w1 and w2:
                split.append({"t0": p["t0"], "t1": tc, "words": w1})
                split.append({"t0": tc, "t1": p["t1"], "words": w2})
                continue
        split.append(p)
    # голос вне строк — распев
    covered = np.zeros(n, dtype=bool)
    for p in split:
        covered[int(p["t0"] * FPS):int(p["t1"] * FPS)] = True
    for r0, r1 in runs(voiced & ~covered):
        if r1 - r0 >= 0.6 * FPS:
            split.append({"t0": r0 / FPS, "t1": r1 / FPS, "words": []})
    split.sort(key=lambda p: p["t0"])
    for p in split:
        p["en"] = " ".join(w[2] for w in p["words"])
    return split


def units_of(p: dict, f0: np.ndarray, onset: np.ndarray, beats: np.ndarray, downbeats: np.ndarray) -> list[dict]:
    """Слоги фразы: ноты из тона → столько, сколько слогов в английской строке (мелизм — одна нота на несколько)."""
    a, b = int(p["t0"] * FPS), int(p["t1"] * FPS) + 1
    notes = A.notes_from_f0(f0[a:b], onset[a:b] if onset is not None else None)
    units = [{"t0": a / FPS + n[0], "t1": a / FPS + n[1], "p": n[2]} for n in notes]
    if not units:
        units = [{"t0": p["t0"], "t1": p["t1"], "p": float(np.median(C.hz_to_midi(f0[a:b][f0[a:b] > 0])) or 60)}]
    k = sum(en_syllables(w[2]) for w in p["words"]) or len(units)
    starts = [w[0] for w in p["words"]]
    units = fit_units(units, k, starts, onset)
    med = float(np.median([u["t1"] - u["t0"] for u in units]))
    for u in units:
        d = u["t1"] - u["t0"]
        on_bar = downbeats.size and np.min(np.abs(downbeats - u["t0"])) < 0.07
        u["strong"] = bool(d >= 1.35 * med or on_bar)
        u["long"] = bool(d >= 0.6)
        u["t0"], u["t1"], u["p"] = round(u["t0"], 3), round(u["t1"], 3), round(float(u["p"]), 2)
    return units


def fit_units(units: list[dict], k: int, word_starts: list[float] | None = None, onset: np.ndarray | None = None) -> list[dict]:
    """Подогнать слоги к числу k: лишние — склеить по самой слабой границе (мелизм), недостающие — поделить длинные ноты."""
    units = [dict(u) for u in units]
    word_starts = word_starts or []

    def strength(i: int) -> float:
        a, b = units[i], units[i + 1]
        s = 10 * max(0.0, b["t0"] - a["t1"]) + 0.3 * abs(b["p"] - a["p"])
        if any(abs(ws - b["t0"]) < 0.08 for ws in word_starts):
            s += 1.5
        if onset is not None:
            j = int(b["t0"] * FPS)
            if 0 <= j < len(onset):
                s += float(onset[j] / (np.percentile(onset, 95) + 1e-9))
        return s

    while len(units) > max(1, k):
        i = min(range(len(units) - 1), key=strength)
        a, b = units[i], units[i + 1]
        longer = a if a["t1"] - a["t0"] >= b["t1"] - b["t0"] else b
        units[i] = {"t0": a["t0"], "t1": b["t1"], "p": longer["p"]}
        del units[i + 1]
    while len(units) < k:
        i = max(range(len(units)), key=lambda j: units[j]["t1"] - units[j]["t0"])
        u = units[i]
        mid = (u["t0"] + u["t1"]) / 2
        units[i:i + 1] = [{"t0": u["t0"], "t1": mid, "p": u["p"]}, {"t0": mid, "t1": u["t1"], "p": u["p"]}]
    return units


def rhythm(units: list[dict]) -> str:
    return " ".join(("ТА" if u["strong"] else "та") + ("—" if u["long"] else "") for u in units)


def make_plan(track: str | Path, progress: C.Progress = C.no_progress, stt_url: str | None = "http://127.0.0.1:8097") -> dict:
    """Разбор (если ещё не было) → фразы и слоги → plan.json и лист для перевода translate.txt."""
    an = A.analyze(track, progress=progress, stt_url=stt_url)
    d = Path(an["dir"])
    f0 = np.load(d / an["vocal_f0"])
    v = C.mono(C.read(d / "vocals.flac"))
    onset = A.onset_env(v, C.SR)
    beats, downbeats = np.asarray(an["beats"]), np.asarray(an["downbeats"])
    words = an.get("lyrics", {}).get("words", [])
    lines = []
    for i, p in enumerate(phrases_of(f0, words, an.get("lyrics", {}).get("lines", []))):
        units = units_of(p, f0, onset, beats, downbeats)
        lines.append({"i": i, "t0": round(p["t0"], 3), "t1": round(p["t1"], 3), "en": p["en"], "k": len(units),
                      "vocalise": not p["words"], "units": units, "rhythm": rhythm(units)})
    med = float(np.median(C.hz_to_midi(f0[f0 > 0]))) if np.any(f0 > 0) else 60.0
    shift = 12 * int(round((KLAVA_CENTER - med) / 12))
    plan = {"track": str(d), "source": an["source"], "title": an.get("meta", {}).get("title") or Path(an["source"]).stem,
            "artist": an.get("meta", {}).get("artist", ""), "language": an.get("lyrics", {}).get("language", ""),
            "median_midi": round(med, 2), "shift": shift, "key": an.get("key", {}).get("name", ""), "lines": lines}
    C.save_json(d / "plan.json", plan)
    (d / "translate.txt").write_text(translate_sheet(plan), encoding="utf-8")
    progress(1.0, f"фраз {len(lines)}, слогов {sum(l['k'] for l in lines)}; сдвиг на {shift:+d} полутонов")
    return plan


def translate_sheet(plan: dict) -> str:
    out = [f"# {plan.get('artist', '')} — {plan['title']}: лист для эквиритмического перевода",
           "# строка: номер | время | слогов | ритм (ТА — сильная нота, «—» — долгая) | оригинал",
           "# русская строка — РОВНО столько слогов (гласных); ударения — на ТА; «=» — спеть оригинал, пусто — промолчать"]
    for l in plan["lines"]:
        out.append(f"{l['i']:02d} | {C.fmt_time(l['t0'])} | {l['k']} | {l['rhythm']} | "
                   f"{l['en'] or '(распев без слов)'}")
    return "\n".join(out)


def read_lyrics(path: Path, n: int) -> list[str | None]:
    """Русские строки по номерам фраз: «NN | текст» или просто строки по порядку. None — строки нет (промолчать)."""
    rows = [r.rstrip("\n") for r in path.read_text(encoding="utf-8-sig").splitlines()]
    rows = [r for r in rows if r.strip() and not r.lstrip().startswith("#")]
    out: list[str | None] = [None] * n
    numbered = all(re.match(r"^\s*\d+\s*\|", r) for r in rows) and rows
    for j, r in enumerate(rows):
        if numbered:
            num, _, txt = r.partition("|")
            i = int(num)
        else:
            i, txt = j, r
        if 0 <= i < n:
            out[i] = txt.strip()
    return out


def stresses(text: str, tts_url: str) -> list[int]:
    """Номера ударных слогов строки (ударения — акцентуатор сервера голоса)."""
    req = urllib.request.Request(tts_url.rstrip("/") + "/accent", data=json.dumps({"text": text}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        marked = json.loads(r.read().decode("utf-8"))["text"]
    out, n, plus = [], 0, False
    for ch in marked.lower():
        if ch == "+":
            plus = True
            continue
        if ch in RU_V:
            if plus or ch == "ё":
                out.append(n)
            n += 1
        plus = False
    return out


def check_lyrics(plan: dict, ru: list[str | None], tts_url: str = TTS_URL) -> list[dict]:
    """Проверка перевода: слогов ровно столько же, ударения на сильных нотах (доля совпадений)."""
    res = []
    for l, t in zip(plan["lines"], ru):
        if t is None or t == "=":
            res.append({"i": l["i"], "ok": True, "skip": True})
            continue
        n = ru_syllables(t)
        st = []
        try:
            st = stresses(t, tts_url)
        except OSError:
            pass
        strong = {i for i, u in enumerate(l["units"]) if u["strong"]}
        hit = sum(1 for s in st if s in strong)
        res.append({"i": l["i"], "ok": n == l["k"], "k": l["k"], "n": n, "stress": st,
                    "stress_on_strong": round(hit / max(1, len(st)), 2), "text": t})
    return res


# ---------- русские фонемы для выравнивания (wav2vec2 xlsr-53 espeak) ----------

RU_SETS = {
    "а": ["a", "ɐ", "ə", "æ", "ä", "ɑ", "ʌ", "aː"], "я": ["a", "ɐ", "ə", "æ", "ɪ", "ja", "ä", "e"], "о": ["o", "ɐ", "ə", "ɔ", "ʌ", "oː", "a"],
    "ё": ["o", "ɵ", "ɔ", "oː"], "е": ["e", "ɛ", "ɪ", "i", "e̞", "eː", "ə"], "э": ["ɛ", "e", "ɪ", "e̞", "ə"],
    "и": ["i", "ɪ", "ɨ", "iː", "ᵻ"], "ы": ["ɨ", "ɪ", "i", "ᵻ", "ɯ"], "у": ["u", "ʊ", "uː", "ʉ"], "ю": ["u", "ʊ", "ʉ", "ju", "uː"],
    "б": ["b", "bʲ", "p"], "в": ["v", "vʲ", "f", "ʋ", "w", "β"], "г": ["ɡ", "ɡʲ", "k", "ɣ", "x"], "д": ["d", "dʲ", "t", "tʲ"],
    "ж": ["ʐ", "ʒ", "ʂ", "ʃ"], "з": ["z", "s", "ʑ"], "й": ["j", "i", "ɪ"], "к": ["k", "kʲ", "ɡ"], "л": ["l", "ɫ", "ʎ", "ɭʲ", "ɭ"],
    "м": ["m", "mʲ"], "н": ["n", "nʲ", "ɲ"], "п": ["p", "pʲ", "b"], "р": ["r", "rʲ", "ɾ", "ɹ", "ʁ"], "с": ["s", "sʲ", "z"],
    "т": ["t", "tʲ", "d"], "ф": ["f", "fʲ", "v"], "х": ["x", "xʲ", "h", "ç"], "ц": ["ts", "tsʲ", "s"],
    "ч": ["tɕ", "tʃ", "tʃʲ", "ɕ", "tɕʲ"], "ш": ["ʂ", "ʃ", "ʂʲ"], "щ": ["ɕ", "ʃ", "ɕʲ", "ʂ"],
}


class Aligner:
    """CTC-выравнивание по множествам фонем: для каждой буквы — любая из близких фонем распознавателя
    (безударное «о» может прозвучать [ɐ] или [ə]); результат — время «пика» каждой буквы-звука."""

    def __init__(self, device: str):
        import torch
        from transformers import Wav2Vec2ForCTC
        self.torch = torch
        self.model = Wav2Vec2ForCTC.from_pretrained(str(C.PHONE_DIR)).to(device).eval()
        self.device = device
        vocab = json.loads((C.PHONE_DIR / "vocab.json").read_text(encoding="utf-8"))
        self.ids = {k: [vocab[t] for t in v if t in vocab] for k, v in RU_SETS.items()}
        self.blank = vocab.get("<pad>", 0)

    def tokens(self, text: str) -> list[tuple[str, list[int], bool]]:
        """(буква, id фонем, гласная ли) — ь/ъ пропускаются, двойные согласные — один звук, «тся/ться» — [ts]."""
        t = text.lower().replace("ться", "ца").replace("тся", "ца")
        out: list[tuple[str, list[int], bool]] = []
        for ch in t:
            if ch not in self.ids:
                continue
            vow = ch in RU_V
            if out and not vow and out[-1][0] == ch:
                continue
            out.append((ch, self.ids[ch], vow))
        return out

    def logits(self, wav16: np.ndarray) -> np.ndarray:
        torch = self.torch
        x = (wav16 - wav16.mean()) / (wav16.std() + 1e-7)
        with torch.inference_mode():
            lg = self.model(torch.from_numpy(x).float()[None].to(self.device)).logits[0]
            return torch.log_softmax(lg.float(), dim=-1).cpu().numpy()

    def align(self, wav16: np.ndarray, text: str) -> list[tuple[str, bool, float]]:
        """[(буква, гласная, время пика в секундах)] по порядку."""
        toks = self.tokens(text)
        if not toks:
            return []
        lp = self.logits(wav16)
        T = lp.shape[0]
        em = np.stack([np.logaddexp.reduce(lp[:, ids], axis=1) if ids else np.full(T, -30.0) for _, ids, _ in toks], axis=1)
        S = 2 * len(toks) + 1
        E = np.empty((T, S), dtype=np.float64)
        E[:, 0::2] = lp[:, self.blank][:, None]
        E[:, 1::2] = em
        neg = -1e18
        dp = np.full(S, neg)
        dp[0], dp[1] = E[0, 0], E[0, 1]
        back = np.zeros((T, S), dtype=np.int8)
        can_skip = np.zeros(S, dtype=bool)
        for s in range(3, S, 2):
            can_skip[s] = toks[(s - 1) // 2][1] != toks[(s - 3) // 2][1]
        for t in range(1, T):
            stay = dp
            prev1 = np.concatenate([[neg], dp[:-1]])
            prev2 = np.concatenate([[neg, neg], dp[:-2]])
            prev2 = np.where(can_skip, prev2, neg)
            best = np.maximum(np.maximum(stay, prev1), prev2)
            back[t] = np.where(best == stay, 0, np.where(best == prev1, 1, 2))
            dp = best + E[t]
        s = S - 1 if dp[S - 1] >= dp[S - 2] else S - 2
        frames: dict[int, list[int]] = {}
        for t in range(T - 1, -1, -1):
            if s % 2 == 1:
                frames.setdefault((s - 1) // 2, []).append(t)
            s -= int(back[t, s])
            if s < 0:
                break
        step = 0.02
        out = []
        for i, (ch, _, vow) in enumerate(toks):
            fr = frames.get(i)
            tc = (float(np.mean(fr)) + 0.5) * step if fr else float("nan")
            out.append((ch, vow, tc))
        # дыры (буква без кадров) — между соседями
        ts = np.array([o[2] for o in out])
        good = ~np.isnan(ts)
        if good.any() and not good.all():
            ts[~good] = np.interp(np.flatnonzero(~good), np.flatnonzero(good), ts[good])
        return [(o[0], o[1], float(t)) for o, t in zip(out, ts)]


def syllable_marks(al: list[tuple[str, bool, float]], speech_end: float) -> list[dict]:
    """Слоги прочитанной строки: начало согласных (все согласные между гласными — к следующему слогу, так поют),
    начало и конец гласной; у последнего слога — хвост согласных."""
    idx = [i for i, a in enumerate(al) if a[1]]
    sy = []
    for j, vi in enumerate(idx):
        prev_v = idx[j - 1] if j else -1
        cons = list(range(prev_v + 1, vi))
        tv = al[vi][2]
        tc_prev = al[vi - 1][2] if vi > 0 else tv - 0.06
        v0 = (tc_prev + tv) / 2 if cons else tv - 0.03
        c0 = (al[cons[0]][2] + (al[cons[0] - 1][2] if cons[0] > 0 else al[cons[0]][2] - 0.08)) / 2 if cons else v0
        sy.append({"c0": max(0.0, c0), "v0": max(0.0, v0), "tv": tv})
    for j, s in enumerate(sy):
        if j + 1 < len(sy):
            s["v1"] = sy[j + 1]["c0"]
            s["end"] = s["v1"]
        else:
            tail = list(range(idx[-1] + 1, len(al)))
            if tail:
                s["v1"] = (al[idx[-1]][2] + al[tail[0]][2]) / 2
                s["end"] = min(speech_end, al[tail[-1]][2] + 0.07)
            else:
                s["v1"] = s["end"] = speech_end
        s["v1"] = max(s["v1"], s["v0"] + 0.03)
        s["end"] = max(s["end"], s["v1"])
    return sy


# ---------- голос Клавы: прочитать строку ----------

def tts(text: str, url: str = TTS_URL, emotion: str = "") -> tuple[np.ndarray, int]:
    import soundfile as sf
    req = urllib.request.Request(url.rstrip("/") + "/tts", data=json.dumps({"text": text, "emotion": emotion}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        w, sr = sf.read(io.BytesIO(r.read()), dtype="float32")
    return C.mono(w) if w.ndim > 1 else w, sr


def speech_bounds(w: np.ndarray, sr: int) -> tuple[float, float]:
    rms = A.frame_rms(w, sr, 100)
    if rms.size == 0:
        return 0.0, len(w) / sr
    on = np.flatnonzero(rms > max(1e-4, rms.max() * 0.03))
    if on.size == 0:
        return 0.0, len(w) / sr
    return on[0] / 100, (on[-1] + 1) / 100


# ---------- WORLD: речь → пение по нотам ----------

class Guide:
    """Направляющий голос на всю песню (24 кГц): строки кладутся на свои места."""

    def __init__(self, duration: float, sr: int = 24000):
        self.sr = sr
        self.buf = np.zeros(int(duration * sr) + sr, dtype=np.float32)

    def add(self, t0: float, w: np.ndarray) -> None:
        a = int(t0 * self.sr)
        if a < 0:
            w, a = w[-a:], 0
        n = min(len(w), len(self.buf) - a)
        if n > 0:
            self.buf[a:a + n] += w[:n]


def legato_f0(f0: np.ndarray, max_gap: float = 0.35) -> np.ndarray:
    """Тон оригинала без щелей короче max_gap: между звучащими кадрами — плавный подъезд (как певица переходит
    с ноты на ноту). Друг 30.09: «поёт отрывками» — на месте этих щелей направляющий голос замолкал."""
    out = f0.astype(np.float64).copy()
    idx = np.flatnonzero(out > 0)
    if idx.size < 2:
        return out
    lf = np.log(out[idx])
    gaps = np.diff(idx)
    for k in np.flatnonzero((gaps > 1) & (gaps <= max_gap * FPS)):
        a, b = idx[k], idx[k + 1]
        out[a + 1:b] = np.exp(np.interp(np.arange(a + 1, b), [a, b], [lf[k], lf[k + 1]]))
    return out


def f0_at(f0: np.ndarray, t: np.ndarray) -> np.ndarray:
    i = np.clip(np.round(t * FPS).astype(int), 0, len(f0) - 1)
    return f0[i]


def sing_line(speech: np.ndarray, sr: int, marks: list[dict], units: list[dict], f0o: np.ndarray, shift: int,
              line_end: float, legato: float = 0.35) -> tuple[float, np.ndarray]:
    """Прочитанная строка → спетая: гласные растянуты на ноты и тянутся до следующего слога (легато), согласные —
    коротко перед нотой, как поют, тон — оригинал со сдвигом (с подъездами между нотами)."""
    import pyworld as pw
    x = speech.astype(np.float64)
    f0s, tt = pw.harvest(x, sr, frame_period=WFP, f0_floor=70, f0_ceil=800)
    sp = pw.cheaptrick(x, f0s, tt, sr)
    ap = pw.d4c(x, f0s, tt, sr)
    nf = len(f0s)
    fp = WFP / 1000
    # отрезки: (цель0, цель1, исход0, исход1, вид)
    seg: list[tuple[float, float, float, float, str, int]] = []
    n = len(units)
    # легато: между нотами короче legato — не пауза, а переход; гласная тянется до следующего слога
    ends = [u["t1"] for u in units]
    for i in range(n - 1):
        if 0 < units[i + 1]["t0"] - units[i]["t1"] < legato:
            ends[i] = units[i + 1]["t0"]
    for i, (m, u) in enumerate(zip(marks, units)):
        S, E = u["t0"], u["t1"]
        cons = max(0.0, m["v0"] - m["c0"])
        prev_end = ends[i - 1] if i else S - 0.3
        prev_len = ends[i - 1] - units[i - 1]["t0"] if i else 0.3
        room = max(0.0, S - prev_end) + 0.3 * prev_len
        # в пении согласные короткие: слово держится на гласных
        d = min(cons, 0.11, room)
        if d > 0.005:
            seg.append((S - d, S, m["c0"], m["v0"], "c", i))
            if i and S - d < ends[i - 1]:
                ends[i - 1] = S - d
    for i, (m, u) in enumerate(zip(marks, units)):
        S = u["t0"]
        last = i == n - 1
        coda = min(max(0.0, m["end"] - m["v1"]), 0.18, 0.35 * (u["t1"] - u["t0"])) if last else 0.0
        E = (min(ends[i], line_end) if not last else u["t1"]) - coda
        E = max(E, S + 0.04)
        v0, v1 = m["v0"], m["v1"]
        a = min(0.03, (v1 - v0) / 4)
        if E - S > 2 * a + 0.01:
            seg.append((S, S + a, v0, v0 + a, "v", i))
            seg.append((S + a, E - a, v0 + a, v1 - a, "w" if (E - S) > 3 * (v1 - v0) and v1 - v0 > 0.05 else "v", i))
            seg.append((E - a, E, v1 - a, v1, "v", i))
        else:
            seg.append((S, E, v0, v1, "v", i))
        if last and coda > 0.005:
            seg.append((E, E + coda, v1, m["end"], "c", i))
    seg.sort()
    t_start = seg[0][0] - 0.02
    t_end = max(s[1] for s in seg) + 0.05
    nt = int((t_end - t_start) / fp) + 1
    tau = t_start + np.arange(nt) * fp
    src = np.full(nt, -1.0)
    kind = np.zeros(nt, dtype=np.int8)       # 0 тишина, 1 согласная, 2 гласная
    unit_of = np.full(nt, -1)
    for s0, s1, a0, a1, k, ui in seg:
        m = (tau >= s0) & (tau < s1)
        if not m.any():
            continue
        lin = a0 + (tau[m] - s0) / max(1e-6, s1 - s0) * (a1 - a0)
        if k == "w":
            # долгая нота из короткой гласной речи: замершая середина звучит роботом — пусть середина гласной
            # медленно ходит туда-обратно (треугольник ~0,45 с), а у краёв плавно возвращается к обычной растяжке
            c, h = (a0 + a1) / 2, (a1 - a0) * 0.3
            ph = (tau[m] - s0) / 0.45
            tri = 2 * np.abs(ph - np.floor(ph + 0.5))
            wgt = np.minimum(1.0, np.minimum(tau[m] - s0, s1 - tau[m]) / 0.06)
            lin = (1 - wgt) * lin + wgt * (c - h + 2 * h * tri)
        src[m] = lin
        kind[m] = 1 if k == "c" else 2
        unit_of[m] = ui
    fi = np.clip(src / fp, 0, nf - 1)
    lo = np.floor(fi).astype(int)
    hi = np.minimum(lo + 1, nf - 1)
    w = (fi - lo)[:, None]
    lsp = np.log(sp + 1e-16)
    sp_t = np.exp((1 - w) * lsp[lo] + w * lsp[hi])
    ap_t = (1 - w) * ap[lo] + w * ap[hi]
    src_voiced = f0s[np.round(fi).astype(int)] > 0
    # тон: оригинал со сдвигом на целые октавы; в гласной, где оригинал «потерял» тон, — ближайший звучащий в этой ноте
    tgt = f0_at(f0o, tau) * (2 ** (shift / 12))
    f0_t = np.zeros(nt)
    for ui, u in enumerate(units):
        m = unit_of == ui
        if not m.any():
            continue
        vals = tgt[m & (kind == 2)]
        fill = float(np.median(vals[vals > 0])) if np.any(vals > 0) else float(C.midi_to_hz(np.array([u["p"] + shift]))[0])
        mv = m & (kind == 2)
        f0_t[mv] = np.where(tgt[mv] > 0, tgt[mv], fill)
        mc = m & (kind == 1) & src_voiced
        f0_t[mc] = fill
    ap_t[(kind == 2) & ~src_voiced] = np.minimum(ap_t[(kind == 2) & ~src_voiced], 0.2)
    sil = kind == 0
    sp_t[sil] = 1e-16
    ap_t[sil] = 1.0
    f0_t[sil] = 0.0
    y = pw.synthesize(f0_t, np.ascontiguousarray(sp_t), np.ascontiguousarray(ap_t), sr, frame_period=WFP)
    return t_start, y.astype(np.float32)


def resing_original(v: np.ndarray, sr: int, t0: float, t1: float, shift: int) -> tuple[float, np.ndarray]:
    """Строка «=» или распев без слов: оригинальный голос через WORLD со сдвигом тона на октавы (форманты свои)."""
    import pyworld as pw
    a, b = max(0, int((t0 - 0.08) * sr)), min(len(v), int((t1 + 0.15) * sr))
    x = v[a:b].astype(np.float64)
    if shift == 0:
        return a / sr, x.astype(np.float32)
    f0, tt = pw.harvest(x, sr, frame_period=WFP, f0_floor=60, f0_ceil=900)
    sp = pw.cheaptrick(x, f0, tt, sr)
    ap = pw.d4c(x, f0, tt, sr)
    y = pw.synthesize(f0 * 2 ** (shift / 12), sp, ap, sr, frame_period=WFP)
    return a / sr, y.astype(np.float32)


# ---------- Seed-VC: живое пение её тембром ----------

class Singer:
    """Seed-VC с тоном (DiT whisper-base f0, BigVGAN 44 кГц): тембр — эталон Клавы, тон — как в источнике (не
    подтягивается к её среднему: мелодия уже в нужной октаве).

    Замер 30.09 на «Lately»: полная перерисовка с шума теряла русские слова (whisper по её голосу: 0,60–0,76 ошибок
    по словам против 0,20 у направляющего голоса; шаги, CFG и число квантователей не помогали). Поэтому диффузия
    стартует не с шума, а с направляющего голоса, «зашумлённого» до точки keep (SDEdit): при keep=0,7 — 0,24 ошибок
    при том же тембре 0,92; звук всё равно рождает BigVGAN из спектра, «жужжание» WORLD уходит."""

    def __init__(self, steps: int = 30, cfg_rate: float = 0.7, keep: float = 0.65):
        import librosa
        import torch
        import torchaudio
        from huggingface_hub import hf_hub_download
        if str(C.SEED) not in sys.path:
            sys.path.append(str(C.SEED))
        if str(C.SEED / "_pkgs") not in sys.path:
            sys.path.append(str(C.SEED / "_pkgs"))
        import os
        cwd = os.getcwd()
        os.chdir(C.SEED)
        try:
            import inference as vc

            def from_cache(repo_id, model_filename="pytorch_model.bin", config_filename=None):
                m = hf_hub_download(repo_id, model_filename)
                return (m, hf_hub_download(repo_id, config_filename)) if config_filename else m
            vc.load_custom_model_from_hf = from_cache
            from modules.bigvgan import bigvgan
            orig = bigvgan.BigVGAN._from_pretrained.__func__

            def bigvgan_from_pretrained(cls, *, proxies=None, resume_download=False, **kw):
                return orig(cls, proxies=proxies, resume_download=resume_download, **kw)
            bigvgan.BigVGAN._from_pretrained = classmethod(bigvgan_from_pretrained)
            ck = hf_hub_download("Plachta/Seed-VC", "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth")
            cf = hf_hub_download("Plachta/Seed-VC", "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml")
            args = SimpleNamespace(fp16=True, f0_condition=True, checkpoint=ck, config=cf)
            (self.model, self.semantic_fn, self.f0_fn, self.vocoder_fn, self.campplus, self.mel_fn, mel_args) = vc.load_models(args)
        finally:
            os.chdir(cwd)
        self.vc, self.torch, self.ta = vc, torch, torchaudio
        self.sr = mel_args["sampling_rate"]
        self.steps, self.cfg_rate, self.keep = steps, cfg_rate, keep
        ref = librosa.load(str(REF), sr=self.sr)[0][: self.sr * 10]
        self.ref = torch.tensor(ref).unsqueeze(0).float().to(vc.device)
        ref16 = torchaudio.functional.resample(self.ref, self.sr, 16000)
        with torch.no_grad():
            self.S_ori = self.semantic_fn(ref16)
            self.mel2 = self.mel_fn(self.ref)
            feat2 = torchaudio.compliance.kaldi.fbank(ref16, num_mel_bins=80, dither=0, sample_frequency=16000)
            self.style2 = self.campplus((feat2 - feat2.mean(dim=0, keepdim=True)).unsqueeze(0))
            self.f0_ref = torch.from_numpy(self.f0_fn(ref16[0], thred=0.03)).to(vc.device)[None]

    def __call__(self, src: np.ndarray) -> np.ndarray:
        torch, vc = self.torch, self.vc
        with torch.no_grad():
            s = torch.tensor(src).unsqueeze(0).float().to(vc.device)
            s16 = self.ta.functional.resample(s, self.sr, 16000)
            S_alt = self.semantic_fn(s16)
            mel = self.mel_fn(s)
            f0 = torch.from_numpy(self.f0_fn(s16[0], thred=0.03)).to(vc.device)[None]
            cond = self.model.length_regulator(S_alt, ylens=torch.LongTensor([mel.size(2)]).to(mel.device), n_quantizers=3, f0=f0)[0]
            prompt = self.model.length_regulator(self.S_ori, ylens=torch.LongTensor([self.mel2.size(2)]).to(mel.device),
                                                 n_quantizers=3, f0=self.f0_ref)[0]
            cat = torch.cat([prompt, cond], dim=1)
            cfm = self.model.cfm
            pl, T = self.mel2.size(-1), cat.size(1)
            lens = torch.LongTensor([T]).to(mel.device)
            with torch.autocast(device_type=vc.device.type, dtype=torch.float16):
                if self.keep > 0:
                    # SDEdit: x_t = (1 − (1 − σ)·t)·шум + t·спектр направляющего голоса, дальше — ОДУ от t до 1
                    x1 = torch.zeros([1, cfm.in_channels, T], device=mel.device)
                    x1[..., pl:pl + mel.size(2)] = mel
                    sig = float(getattr(cfm, "sigma_min", 1e-6))
                    x = (1 - (1 - sig) * self.keep) * torch.randn_like(x1) + self.keep * x1
                    t_span = torch.linspace(self.keep, 1, self.steps + 1, device=mel.device)
                    out = cfm.solve_euler(x, lens, self.mel2, cat, self.style2, None, t_span, self.cfg_rate)
                else:
                    out = cfm.inference(cat, lens, self.mel2, self.style2, None, self.steps, inference_cfg_rate=self.cfg_rate)
            out = out[:, :, pl:]
            wave = self.vocoder_fn(out.float()).squeeze().cpu().numpy()
        n = len(src)
        return np.pad(wave, (0, max(0, n - len(wave))))[:n].astype(np.float32)


def voice_chunks(x: np.ndarray, sr: int, max_len: float = 16.0) -> list[tuple[int, int]]:
    """Куски голоса для Seed-VC: звучащее, режется по тишине, не длиннее max_len (контекст DiT — 30 с вместе с эталоном)."""
    rms = A.frame_rms(x, sr, 100)
    act = rms > max(1e-5, np.percentile(rms[rms > 0], 90) * 0.01) if np.any(rms > 0) else np.zeros(len(rms), bool)
    regs = runs(act)
    merged: list[list[int]] = []
    for r in regs:
        if merged and r[0] - merged[-1][1] < 25 and (r[1] - merged[-1][0]) < max_len * 100:
            merged[-1][1] = r[1]
        else:
            merged.append(list(r))
    out = []
    for a, b in merged:
        a, b = max(0, a - 5), min(len(rms), b + 10)
        while b - a > max_len * 100:
            cut = a + int(max_len * 100)
            seg = rms[a + int(max_len * 50):cut]
            k = a + int(max_len * 50) + int(np.argmin(seg)) if seg.size else cut
            out.append((a * sr // 100, k * sr // 100))
            a = k
        out.append((a * sr // 100, b * sr // 100))
    return out


# ---------- сведение ----------

def envelope(x: np.ndarray, sr: int, win: float = 0.05) -> np.ndarray:
    from scipy.ndimage import uniform_filter1d
    return np.sqrt(uniform_filter1d(x.astype(np.float64) ** 2, size=max(1, int(win * sr))) + 1e-12)


def match_dynamics(y: np.ndarray, ref: np.ndarray, sr: int) -> np.ndarray:
    """Громкость по оригинальному голосу: крещендо и затихания певицы — у Клавы тоже (±12 дБ, сглажено 80 мс)."""
    n = min(len(y), len(ref))
    ey, er = envelope(y[:n], sr, 0.08), envelope(ref[:n], sr, 0.08)
    act = ey > ey.max() * 0.01
    g = np.ones(n)
    g[act] = np.clip(er[act] / ey[act], 10 ** (-12 / 20), 10 ** (12 / 20))
    from scipy.ndimage import uniform_filter1d
    g = uniform_filter1d(g, size=int(0.08 * sr))
    out = y.copy()
    out[:n] = y[:n] * g
    return out


def compress(x: np.ndarray, sr: int, thr_db: float = -20, ratio: float = 3.0, att: float = 0.005, rel: float = 0.09) -> np.ndarray:
    """Компрессор голоса: уровень блоками по 64 отсчёта, быстрая атака, плавный отпуск."""
    blk = 64
    nb = len(x) // blk + 1
    pad = np.pad(x.astype(np.float64), (0, nb * blk - len(x)))
    lvl = 10 * np.log10(np.mean(pad.reshape(nb, blk) ** 2, axis=1) + 1e-12)
    gr = -np.maximum(0.0, lvl - thr_db) * (1 - 1 / ratio)
    ca, cr = np.exp(-blk / (att * sr)), np.exp(-blk / (rel * sr))
    sm = np.empty(nb)
    cur = 0.0
    for i in range(nb):
        c = ca if gr[i] < cur else cr
        cur = c * cur + (1 - c) * gr[i]
        sm[i] = cur
    g = np.interp(np.arange(len(x)), np.arange(nb) * blk + blk / 2, 10 ** (sm / 20))
    return (x * g).astype(np.float32)


def reverb_ir(sr: int, rt60: float = 1.6, pre: float = 0.025, seed: int = 7) -> np.ndarray:
    rng = np.random.default_rng(seed)
    n = int(rt60 * sr)
    t = np.arange(n) / sr
    decay = np.exp(-6.91 * t / rt60)
    from scipy.signal import butter, lfilter
    ir = np.zeros((n + int(pre * sr), 2), dtype=np.float32)
    for ch in range(2):
        noise = rng.standard_normal(n) * decay
        b, a = butter(2, 5000 / (sr / 2))
        noise = lfilter(b, a, noise)
        ir[int(pre * sr):, ch] = noise
    ir /= np.sqrt((ir ** 2).sum(axis=0, keepdims=True)) + 1e-9
    return ir


def limiter(x: np.ndarray, sr: int, ceil_db: float = -1.0) -> np.ndarray:
    """Пиковый лимитер с заглядыванием на 3 мс: пики не выше потолка, без щелчков."""
    from scipy.ndimage import maximum_filter1d, uniform_filter1d
    c = 10 ** (ceil_db / 20)
    a = np.max(np.abs(x), axis=1)
    g = np.minimum(1.0, c / (maximum_filter1d(a, size=int(0.006 * sr)) + 1e-9))
    g = uniform_filter1d(g, size=int(0.004 * sr))
    g = np.minimum(g, c / (a + 1e-9))
    return (x * g[:, None]).astype(np.float32)


def master(vocal: np.ndarray, orig_vocal: np.ndarray, inst: np.ndarray, orig_mix: np.ndarray, sr: int) -> tuple[np.ndarray, np.ndarray]:
    """Голос Клавы (моно 44,1 кГц) + минус → кавер. Возвращает (кавер стерео, обработанный голос моно)."""
    from scipy.signal import butter, fftconvolve, sosfilt
    n = min(len(vocal), len(inst))
    v = vocal[:n].astype(np.float32)
    v = sosfilt(butter(2, 90 / (sr / 2), "highpass", output="sos"), v).astype(np.float32)
    ov = C.mono(orig_vocal)[:n]
    v = match_dynamics(v, ov, sr)
    v = compress(v, sr)
    lv, _ = C.loudness(v[:, None].repeat(2, axis=1), sr)
    lo, _ = C.loudness(np.stack([ov, ov], axis=1), sr)
    if lv > -69 and lo > -69:
        v = v * 10 ** ((lo - lv) / 20)
    ir = reverb_ir(sr)
    wet = np.stack([fftconvolve(v, ir[:, 0])[:n], fftconvolve(v, ir[:, 1])[:n]], axis=1) * 0.22
    mix = inst[:n] + v[:, None] + wet
    lm, _ = C.loudness(mix, sr)
    lt, _ = C.loudness(orig_mix[:n], sr)
    if lm > -69 and lt > -69:
        mix = mix * 10 ** (np.clip(lt - lm, -6, 6) / 20)
    return limiter(mix, sr), v


# ---------- весь кавер ----------

def cover_dir(plan: dict, mode: str) -> Path:
    d = C.DATA / "covers" / f"{Path(plan['track']).name}-{mode}"
    d.mkdir(parents=True, exist_ok=True)
    return d


def sing(track: str | Path, lyrics: list[str | None] | None, progress: C.Progress = C.no_progress, tts_url: str = TTS_URL,
         orig: bool = False, steps: int = 30, keep: float | None = None, tag: str = "", voice: str = "calm") -> dict:
    """lyrics — русские строки по фразам плана (None — промолчать, «=» — оригинал её тембром); orig — вся песня в оригинале."""
    t_start = time.time()
    d = C.track_dir(track) if Path(track).is_file() else Path(track)
    pj = d / "plan.json"
    plan = json.loads(pj.read_text(encoding="utf-8")) if pj.exists() else make_plan(track, progress)
    an = json.loads((d / "analysis.json").read_text(encoding="utf-8"))
    mode = "orig" if orig else "ru"
    out = cover_dir(plan, mode + (f"-{C.slug(tag)}" if tag else ""))
    f0o = legato_f0(np.load(d / an["vocal_f0"]))
    shift = int(plan["shift"])
    vocals = C.read(d / "vocals.flac")
    v_m = C.mono(vocals)
    duration = float(an["duration"])
    gsr = 24000
    guide = Guide(duration, gsr)
    v24 = C.resample(v_m, C.SR, gsr)
    lines = plan["lines"]
    if orig:
        lyrics = ["="] * len(lines)
    if lyrics is None:
        raise ValueError("нет русского текста: сначала перевод по translate.txt")
    aligner = None
    report = []
    for li, (line, ru) in enumerate(zip(lines, lyrics)):
        progress(0.05 + 0.45 * li / max(1, len(lines)), f"строка {li + 1}/{len(lines)}: {ru or '(молчу)'}")
        if ru is None or not ru.strip():
            continue
        if ru.strip() == "=":
            t0, y = resing_original(v24, gsr, line["t0"], line["t1"], shift)
            guide.add(t0, y)
            report.append({"i": line["i"], "mode": "orig"})
            continue
        n = ru_syllables(ru)
        if n == 0:
            continue
        units = line["units"] if n == len(line["units"]) else fit_units(line["units"], n)
        for u in units:
            u.setdefault("strong", False)
            u.setdefault("long", False)
        # манера чтения: calm (замер 30.09 на «Lately»: tender мягче, но глотает согласные — разборчивость 0,42 против 0,27)
        w, sr = tts(ru, tts_url, voice)
        w16 = C.resample(w, sr, 16000)
        if aligner is None:
            aligner = Aligner(C.device(1.0))
        al = aligner.align(w16, ru)
        _, send = speech_bounds(w, sr)
        marks = syllable_marks(al, send)
        if len(marks) != len(units):
            units = fit_units(units, len(marks))
        ws = C.resample(w, sr, gsr)
        nxt = lines[li + 1]["t0"] if li + 1 < len(lines) else duration
        t0, y = sing_line(ws, gsr, marks, units, f0o, shift, nxt)
        peak = float(np.max(np.abs(y))) or 1.0
        guide.add(t0, y / peak * 0.5)
        report.append({"i": line["i"], "mode": "ru", "k": line["k"], "n": n, "text": ru})
    del aligner
    C.free_gpu()
    C.write(out / "guide.wav", guide.buf, gsr)
    progress(0.52, "Seed-VC: перекрашиваю в мой голос…")
    # оригинальный голос (orig) перекрашивается целиком — там слова и так её не касаются; русский — от направляющего
    singer = Singer(steps=steps, keep=(0.0 if orig else 0.65) if keep is None else keep)
    g44 = C.resample(guide.buf, gsr, singer.sr)
    klava = np.zeros_like(g44)
    chunks = voice_chunks(g44, singer.sr)
    for ci, (a, b) in enumerate(chunks):
        progress(0.52 + 0.38 * ci / max(1, len(chunks)), f"пою кусок {ci + 1}/{len(chunks)}")
        seg = g44[a:b]
        y = singer(seg)
        fade = min(len(y) // 4, int(0.01 * singer.sr))
        if fade > 1:
            y[:fade] *= np.linspace(0, 1, fade)
            y[-fade:] *= np.linspace(1, 0, fade)
        klava[a:b] += y
    klava = C.resample(klava, singer.sr, C.SR)
    del singer
    C.free_gpu()
    progress(0.92, "свожу с минусом…")
    inst = sum(C.read(d / f"{k}.flac") for k in ("drums", "bass", "other"))
    mix_orig = C.read(d / "mix.flac")
    cover, vproc = master(klava, vocals, inst, mix_orig, C.SR)
    C.write(out / "cover.wav", cover)
    C.write(out / "vocal.wav", vproc)
    try:
        import subprocess
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(out / "cover.wav"), "-codec:a", "libmp3lame", "-b:a", "192k",
                        str(out / "cover.mp3")], check=False)
    except OSError:
        pass
    subs = [{"t0": l["t0"], "t1": l["t1"], "text": (r if r and r != "=" else l["en"])} for l, r in zip(lines, lyrics) if r]
    info = {"title": plan["title"], "artist": plan.get("artist", ""), "mode": mode, "dir": str(out), "cover": "cover.wav",
            "mp3": "cover.mp3", "vocal": "vocal.wav", "duration": duration, "beats": an.get("beats", []),
            "downbeats": an.get("downbeats", []), "lines": subs, "shift": shift, "report": report,
            "mood": [r[0] for r in an.get("labels", {}).get("mood", [])[:2]], "seconds": round(time.time() - t_start, 1)}
    C.save_json(out / "cover.json", info)
    progress(1.0, f"кавер готов за {info['seconds']:.0f} с: {out / 'cover.mp3'}")
    return info


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["plan", "check", "sing"])
    ap.add_argument("track")
    ap.add_argument("--lyrics", default=None, help="файл с русскими строками (NN | текст)")
    ap.add_argument("--orig", action="store_true", help="спеть оригинал её тембром")
    ap.add_argument("--steps", type=int, default=30)
    ap.add_argument("--keep", type=float, default=None, help="SDEdit: с какой точки диффузии начинать (0 — с шума)")
    ap.add_argument("--tag", default="", help="вариант кавера — своя папка (не затирать прошлый)")
    ap.add_argument("--voice", default="calm", help="манера чтения строк голосом Клавы (calm; tender мягче, но глотает согласные: разборчивость 0,42 против 0,27)")
    a = ap.parse_args()
    if a.cmd == "plan":
        plan = make_plan(a.track)
        print((Path(plan["track"]) / "translate.txt").read_text(encoding="utf-8"))
        return 0
    d = C.track_dir(a.track)
    plan = json.loads((d / "plan.json").read_text(encoding="utf-8"))
    ru = read_lyrics(Path(a.lyrics), len(plan["lines"])) if a.lyrics else None
    if a.cmd == "check":
        for r in check_lyrics(plan, ru or []):
            print(json.dumps(r, ensure_ascii=False))
        return 0
    info = sing(a.track, ru, orig=a.orig, steps=a.steps, keep=a.keep, tag=a.tag, voice=a.voice)
    print(json.dumps({k: v for k, v in info.items() if k not in ("beats", "downbeats", "lines")}, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
