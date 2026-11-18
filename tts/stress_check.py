"""Проверка ударений в синтезированной речи — без человека, по гласным.

В русском безударные гласные редуцируются и короче ударных: «о» без ударения звучит как [ɐ]/[ə], «е» — как [ɪ],
полное [o]/[e] бывает только под ударением, а ударная гласная ещё и самая длинная и громкая в слове.
Фонемный распознаватель (wav2vec2 xlsr-53 espeak, CTC) пишет, что услышал на месте каждой гласной; к этому —
длительность и громкость гласной. Как эти признаки выглядят у ударных и безударных гласных ЭТОГО распознавателя,
измеряется на Silero с заведомо верной разметкой (простая наивная байесовская модель, models/eval/stress_nb.json),
и дальше для каждого слова сравнивается правдоподобие ударения на каждом слоге.

Ожидаемые фонемы фразы (свой простой G2P) выравниваются с услышанными взвешенной правкой (гласная с гласной почти
бесплатно, звонкая с глухой дёшево) — так каждой гласной слова достаётся её звук.

    python stress_check.py --fit        # обучить модель признаков на Silero (3 голоса) и проверить на двух других
    python stress_check.py <папка с NN.wav> <файл фраз>   # доля слов с ударением не там
"""
from __future__ import annotations

import argparse
import json
import math
import random
import re
import sys
from collections import Counter
from pathlib import Path

import numpy as np
import soundfile as sf
import torch

ROOT = Path(__file__).resolve().parent.parent
PHONE_MODEL = ROOT / "models" / "eval" / "w2v2-phoneme"
NB_FILE = ROOT / "models" / "eval" / "stress_nb.json"
SILERO = ROOT / "models" / "silero_v4_ru.pt"
SENTENCES = Path(__file__).with_name("stress_sentences.txt")
V = "аеёиоуыэюя"
FRAME = 0.02   # шаг распознавателя, с

CONS = {"б": "b", "в": "v", "г": "g", "д": "d", "ж": "ʐ", "з": "z", "й": "j", "к": "k", "л": "l", "м": "m",
        "н": "n", "п": "p", "р": "r", "с": "s", "т": "t", "ф": "f", "х": "x", "ц": "ts", "ч": "tɕ", "ш": "ʂ", "щ": "ɕ"}
SIMILAR = [{"b", "p"}, {"v", "f"}, {"g", "k", "ɡ"}, {"d", "t"}, {"z", "s"}, {"ʐ", "ʂ", "ʃ", "ʒ"}, {"ɕ", "ʃ", "ʂ", "s"},
           {"tɕ", "tʃ", "ts", "t"}, {"r", "ɾ", "ɹ"}, {"l", "ɫ", "ʎ"}, {"n", "ɲ"}, {"x", "h", "ç"}, {"j", "i"}]

CLASS: dict[str, str] = {}
for _c, _phs in {
    "O": ["o", "ɔ", "oː", "o̞", "oʊ", "ɔː", "ɵ", "oɪ", "ɔɪ", "ɔø"],
    "A": ["a", "aː", "ä", "ɑ", "æ", "ɑː", "ja", "aɪ", "aʊ"],
    "E": ["e", "ɛ", "eː", "e̞", "ɛː", "eɪ", "ɛɪ"],
    "I": ["i", "iː", "ɪ", "ɨ", "y", "ʉ", "ᵻ", "i.", "ɪː", "ɨː", "ɯ"],
    "U": ["u", "ʊ", "uː", "ʊː"],
    "R": ["ɐ", "ə", "ʌ", "ɜ", "ɐ̃", "əː", "ɜː", "ɚ"],
}.items():
    for _p in _phs:
        CLASS[_p] = _c
CLASSES = ["O", "A", "E", "I", "U", "R", "-"]


def group(word: str, vi: int) -> str:
    """Группа буквы гласной (разные буквы по-разному редуцируются)."""
    w = word.lower()
    pos = [i for i, c in enumerate(w) if c in V][vi]
    ch, prev = w[pos], w[pos - 1] if pos else ""
    if ch == "а" and prev in "чщ":
        return "я"
    if ch == "э":
        return "е"
    if ch in "ыи":
        return "и"
    if ch in "юу":
        return "у"
    return ch


def g2p(word: str) -> list[tuple[str, int]]:
    """Фонемы слова для выравнивания: согласные как есть, гласные — «V» с номером слога."""
    out, vi = [], 0
    w = word.lower()
    for i, ch in enumerate(w):
        prev = w[i - 1] if i else ""
        if ch in V:
            if ch in "яеёю" and (i == 0 or prev in V or prev in "ьъ"):
                out.append(("j", -1))
            out.append(("V", vi))
            vi += 1
        elif ch in CONS:
            out.append((CONS[ch], -1))
    return out


def cost_sub(a: str, b: str) -> float:
    av, bv = a == "V", b in CLASS
    if av or bv:
        return 0.2 if av and bv else 1.2
    if a == b:
        return 0.0
    return 0.3 if any(a in s and b in s for s in SIMILAR) else 1.0


def align(exp: list[str], got: list[str]) -> list[int]:
    """Для каждого ожидаемого — индекс услышанного (или -1): взвешенная правка и обратный проход."""
    n, m = len(exp), len(got)
    d = np.zeros((n + 1, m + 1), dtype=np.float32)
    d[:, 0] = np.arange(n + 1) * 0.9
    d[0, :] = np.arange(m + 1) * 0.9
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            d[i, j] = min(d[i - 1, j - 1] + cost_sub(exp[i - 1], got[j - 1]), d[i - 1, j] + 0.9, d[i, j - 1] + 0.9)
    i, j, res = n, m, [-1] * n
    while i > 0 and j > 0:
        if abs(d[i, j] - (d[i - 1, j - 1] + cost_sub(exp[i - 1], got[j - 1]))) < 1e-4:
            res[i - 1] = j - 1
            i, j = i - 1, j - 1
        elif abs(d[i, j] - (d[i - 1, j] + 0.9)) < 1e-4:
            i -= 1
        else:
            j -= 1
    return res


class Recognizer:
    def __init__(self, device: str = "cuda" if torch.cuda.is_available() else "cpu"):
        if not (PHONE_MODEL / "pytorch_model.bin").exists():
            raise SystemExit(f"нет фонемного распознавателя {PHONE_MODEL}: скачай файлы "
                             "https://huggingface.co/facebook/wav2vec2-xlsr-53-espeak-cv-ft (config, vocab, pytorch_model.bin)")
        from transformers import Wav2Vec2ForCTC
        self.model = Wav2Vec2ForCTC.from_pretrained(str(PHONE_MODEL)).to(device).eval()
        self.device = device
        vocab = json.loads((PHONE_MODEL / "vocab.json").read_text(encoding="utf-8"))
        self.id2tok = {i: t.replace("ʲ", "") for t, i in vocab.items()}

    @torch.inference_mode()
    def phones(self, wav: np.ndarray, sr: int) -> tuple[list[tuple[str, int, int]], np.ndarray]:
        """Фонемы (токен, первый кадр, последний кадр) и громкость по кадрам 20 мс (16 кГц)."""
        if wav.ndim > 1:
            wav = wav.mean(axis=1)
        if sr != 16000:
            import torchaudio.functional as AF
            wav = AF.resample(torch.from_numpy(np.ascontiguousarray(wav)).float(), sr, 16000).numpy()
        x = (wav - wav.mean()) / (wav.std() + 1e-7)
        ids = self.model(torch.from_numpy(x).float()[None].to(self.device)).logits[0].argmax(-1).tolist()
        toks, last = [], None
        for f, i in enumerate(ids):
            if i > 3 and i == last:
                toks[-1] = (toks[-1][0], toks[-1][1], f)
            elif i > 3:
                toks.append((self.id2tok[i], f, f))
            last = i
        hop = int(16000 * FRAME)
        n = len(wav) // hop
        rms = np.sqrt(np.mean(wav[: n * hop].reshape(n, hop) ** 2, axis=1) + 1e-10) if n else np.zeros(1)
        return toks, rms


def stress_of(marked: str) -> int | None:
    """Номер ударного слога по «+» перед гласной (или ё)."""
    n, hit, idx = 0, False, None
    for ch in marked.lower():
        if ch == "+":
            hit = True
            continue
        if ch in V:
            if hit or (ch == "ё" and idx is None):
                idx = n
            hit = False
            n += 1
    return idx


WORD_RX = re.compile(r"[А-Яа-яЁё+]+(?:-[А-Яа-яЁё+]+)*")


def features(rec: Recognizer, wav: np.ndarray, sr: int, marked_sentence: str) -> list[dict]:
    """По словам фразы (многосложным, без ё): что распознаватель услышал на каждой гласной, её длина и громкость."""
    words = WORD_RX.findall(marked_sentence)
    exp_seq, owner = [], []
    for wi, w in enumerate(words):
        for ph, vi in g2p(w.replace("+", "").replace("-", "")):
            exp_seq.append(ph)
            owner.append((wi, vi))
    toks, rms = rec.phones(wav, sr)
    got = [t for t, _, _ in toks]
    amap = align(exp_seq, got)
    out = []
    for wi, w in enumerate(words):
        plain = w.replace("+", "").replace("-", "").lower()
        nv = sum(c in V for c in plain)
        k = stress_of(w)
        if nv < 2 or k is None or "ё" in plain:
            continue
        slots = []
        for vi in range(nv):
            pos = next((p for p, o in enumerate(owner) if o == (wi, vi)), None)
            j = amap[pos] if pos is not None else -1
            if j < 0 or got[j] not in CLASS:
                slots.append({"cls": "-", "dur": None, "loud": None})
                continue
            # длительность гласной — от центра соседа слева до центра соседа справа (CTC даёт пики, а не отрезки)
            left = (toks[j - 1][1] + toks[j - 1][2]) / 2 if j > 0 else toks[j][1] - 2
            right = (toks[j + 1][1] + toks[j + 1][2]) / 2 if j + 1 < len(toks) else toks[j][2] + 2
            a, b = max(0, int(left)), min(len(rms) - 1, int(math.ceil(right)))
            slots.append({"cls": CLASS[got[j]], "dur": max(1.0, right - left), "loud": float(rms[a:b + 1].max()) if b >= a else None})
        out.append({"word": w, "wi": wi, "plain": plain, "k": k, "slots": slots})
    return out


def rel(slots: list[dict], key: str, vi: int) -> str | None:
    """Самая длинная/громкая в слове гласная — «top», иначе «low» (сравнение внутри слова снимает темп и громкость голоса)."""
    vals = [s[key] for s in slots if s[key] is not None]
    v = slots[vi][key]
    if v is None or len(vals) < 2:
        return None
    return "top" if v >= max(vals) - 1e-9 else "low"


class StressNB:
    """P(признак | группа буквы, ударная или нет) — счётчики с добавочным сглаживанием."""

    def __init__(self, data: dict | None = None):
        self.c = Counter(data or {})

    def add(self, word: dict, k: int) -> None:
        for vi, s in enumerate(word["slots"]):
            st = "S" if vi == k else "U"
            g = group(word["plain"], vi)
            self.c[f"cls|{g}|{st}|{s['cls']}"] += 1
            self.c[f"cls|{g}|{st}"] += 1
            for key in ("dur", "loud"):
                r = rel(word["slots"], key, vi)
                if r is not None:
                    self.c[f"{key}|{st}|{r}"] += 1
                    self.c[f"{key}|{st}"] += 1

    def _p(self, num: str, den: str, n_values: int) -> float:
        return (self.c[num] + 1.0) / (self.c[den] + n_values)

    def loglik(self, word: dict, k: int) -> float:
        ll = 0.0
        for vi, s in enumerate(word["slots"]):
            st = "S" if vi == k else "U"
            g = group(word["plain"], vi)
            if s["cls"] != "-":
                ll += math.log(self._p(f"cls|{g}|{st}|{s['cls']}", f"cls|{g}|{st}", len(CLASSES)))
            for key in ("dur", "loud"):
                r = rel(word["slots"], key, vi)
                if r is not None:
                    ll += 0.5 * math.log(self._p(f"{key}|{st}|{r}", f"{key}|{st}", 2))
        return ll

    def verdict(self, word: dict, margin: float = 1.5) -> dict:
        """Лучший слог и ошибка ли: ожидаемый проигрывает лучшему хотя бы на margin (натуральный лог)."""
        nv = len(word["slots"])
        lls = [self.loglik(word, k) for k in range(nv)]
        best = int(np.argmax(lls))
        return {"word": word["word"], "expected": word["k"], "heard": best,
                "wrong": best != word["k"] and lls[best] - lls[word["k"]] >= margin}

    def save(self) -> None:
        NB_FILE.parent.mkdir(parents=True, exist_ok=True)
        NB_FILE.write_text(json.dumps(dict(self.c), ensure_ascii=False, indent=0), encoding="utf-8")

    @staticmethod
    def load() -> "StressNB":
        return StressNB(json.loads(NB_FILE.read_text(encoding="utf-8")))


def shift(marked: str, rnd: random.Random, n: int) -> tuple[str, set[int]]:
    """Увести ударение у n многосложных слов (без ё) на другой слог. Возвращает текст и номера сдвинутых слов."""
    parts = re.split(r"([А-Яа-яЁё+]+(?:-[А-Яа-яЁё+]+)*)", marked)
    widx = [i for i, p in enumerate(parts) if WORD_RX.fullmatch(p)]
    cand = [(wn, i) for wn, i in enumerate(widx)
            if "ё" not in parts[i].lower() and "-" not in parts[i] and "+" in parts[i]
            and sum(c in V for c in parts[i].lower()) >= 2]
    rnd.shuffle(cand)
    changed = set()
    for wn, i in cand[:n]:
        plain = parts[i].replace("+", "")
        nv = sum(c in V for c in plain.lower())
        k0 = stress_of(parts[i])
        k = rnd.choice([x for x in range(nv) if x != k0])
        pos = [p for p, c in enumerate(plain.lower()) if c in V][k]
        parts[i] = plain[:pos] + "+" + plain[pos:]
        changed.add(wn)
    return "".join(parts), changed


def _silero():
    m = torch.package.PackageImporter(str(SILERO)).load_pickle("tts_models", "model")
    m.to(torch.device("cpu"))
    return m


def fit() -> None:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import ru_text
    ru_text.load()
    rec = Recognizer()
    silero = _silero()
    sentences = [l.strip() for l in open(SENTENCES, encoding="utf-8") if l.strip()]
    marked = [ru_text.prepare(s)[1] for s in sentences]
    nb = StressNB()
    for m in marked:
        for sp in ("aidar", "baya", "eugene"):
            a = silero.apply_tts(text=m, speaker=sp, sample_rate=48000, put_accent=False, put_yo=False).numpy()
            for w in features(rec, a, 48000, m):
                nb.add(w, w["k"])
    nb.save()
    # проверка на голосах, которых модель не видела: верная разметка (ложные тревоги) и сдвинутая (чувствительность)
    rnd = random.Random(7)
    fp = tot = hit = tot_sh = 0
    for m in marked:
        for sp in ("xenia", "kseniya"):
            a = silero.apply_tts(text=m, speaker=sp, sample_rate=48000, put_accent=False, put_yo=False).numpy()
            for w in features(rec, a, 48000, m):
                tot += 1
                fp += nb.verdict(w)["wrong"]
            sh, changed = shift(m, rnd, 2)
            b = silero.apply_tts(text=sh, speaker=sp, sample_rate=48000, put_accent=False, put_yo=False).numpy()
            for w in features(rec, b, 48000, m):
                if w["wi"] in changed:
                    tot_sh += 1
                    hit += nb.verdict(w)["wrong"]
    print(f"верная разметка: ложных тревог {fp}/{tot} = {fp / max(tot, 1):.1%}")
    print(f"сдвинутые слова: поймано {hit}/{tot_sh} = {hit / max(tot_sh, 1):.1%}")


def score_dir(folder: Path, phrases_file: Path) -> None:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import ru_text
    ru_text.load()
    rec = Recognizer()
    nb = StressNB.load()
    phrases = [l.strip() for l in open(phrases_file, encoding="utf-8") if l.strip()]
    bad = tot = 0
    lines = []
    for i, p in enumerate(phrases):
        f = folder / f"{i:02d}.wav"
        if not f.exists():
            continue
        a, sr = sf.read(f, dtype="float32")
        _, marked = ru_text.prepare(p)
        vs = [nb.verdict(w) for w in features(rec, a, sr, marked)]
        tot += len(vs)
        bad += sum(v["wrong"] for v in vs)
        lines.append(f"{i:02d}: " + " ".join(f"{v['word']}{'✗' if v['wrong'] else ''}" for v in vs))
    print("\n".join(lines))
    print(f"{folder.name}: ударение не там {bad}/{tot} = {bad / max(tot, 1):.1%}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", nargs="?")
    ap.add_argument("phrases", nargs="?")
    ap.add_argument("--fit", action="store_true")
    a = ap.parse_args()
    if a.fit:
        fit()
    else:
        score_dir(Path(a.folder), Path(a.phrases))
