"""Русский текст → то, что Qwen3-TTS с адаптером ударений читает правильно.

Qwen3-TTS учили без разметки ударений: сама она ошибается примерно в каждом седьмом слове, «+» читает вслух
как «плюс», а знак U+0301 без дообучения коверкает слово. Адаптер ударений (LoRA v3, olluorg/voicy, ADR 0023)
научил модель слушаться знака U+0301 сразу после ударной гласной — это один токен её словаря, вслух не читается.

Порядок:
  1. чистка: разметка, эмодзи, ссылки, служебные символы (& + * # _ | …) — вслух их не читать;
  2. английские слова и аббревиатуры — русской записью (GPT → джипити), остальная латиница как есть;
  3. числа, проценты, время, деньги — словами с согласованием («5 процентов», «2 процента»);
  4. ударения и ё — RUAccent (словарь Зализняка + нейросеть для незнакомых слов + омографы по контексту),
     поверх — свой словарь tts/custom_stress.json (сленг, мат, IT-глаголы, имена);
  5. знак ставится только в многосложных словах без ё (у ё ударение и так есть), «+» RUAccent → U+0301.

Слова не подменяются: что Клава сказала, то и звучит, меняется только ударение.
"""
from __future__ import annotations

import json
import re
import threading
import unicodedata
from pathlib import Path

from num2words import num2words

ROOT = Path(__file__).resolve().parent
CUSTOM_FILE = ROOT / "custom_stress.json"
ACUTE = "́"
VOWELS = "аеёиоуыэюяАЕЁИОУЫЭЮЯ"

# Английское и аббревиатуры: Qwen читает латинские аббревиатуры по-английски или по буквам, а в русской речи
# их говорят так. Ключи — в нижнем регистре; многословные и с точкой ищутся целиком.
TRANSLIT: dict[str, str] = {
    "deepseek": "дипсик", "antigravity": "антигравити", "gemini": "джемини", "gpt": "джипити",
    "chatgpt": "чат джипити", "claude": "клод", "claude code": "клод код", "qwen": "квен", "qwen3": "квен три",
    "sonnet": "сонет", "opus": "опус", "haiku": "хайку", "llama": "лама", "llama.cpp": "лама си пи пи",
    "whisper": "виспер", "ollama": "оллама", "lm studio": "эл эм студио", "copilot": "копайлот",
    "cuda": "куда", "vram": "видеопамять", "gpu": "видеокарта", "cpu": "процессор", "ram": "оперативка",
    "llm": "эл эл эм", "tts": "тэ тэ эс", "api": "апи", "sdk": "эс дэ ка", "cli": "си эл ай", "ui": "ю ай",
    "http": "эйч ти ти пи", "https": "эйч ти ти пи эс", "url": "ю эр эл", "json": "джейсон", "xml": "икс эм эл",
    "html": "эйч ти эм эл", "css": "си эс эс", "js": "джей эс", "usb": "ю эс би", "hdmi": "эйч ди эм ай",
    "rgb": "эр джи би", "png": "пи эн джи", "jpg": "джипег", "jpeg": "джипег", "gif": "гиф", "pdf": "пэ дэ эф",
    "exe": "экзешник", "dll": "дэ эл эл", "mp3": "эм пэ три", "mp4": "эм пэ четыре", "vpn": "впн", "dns": "дэ эн эс",
    "ip": "ай пи", "ai": "эй ай", "ok": "окей", "okay": "окей", "windows": "виндоус", "linux": "линукс",
    "github": "гитхаб", "git": "гит", "docker": "докер", "python": "питон", "c#": "си шарп", "c++": "си плюс плюс",
    ".net": "дот нет", "dotnet": "дот нет", "vs code": "вэ эс код", "vscode": "вэ эс код", "youtube": "ютуб",
    "twitch": "твич", "discord": "дискорд", "steam": "стим", "telegram": "телеграм", "google": "гугл",
    "microsoft": "майкрософт", "nvidia": "энвидиа", "rtx": "эр тэ икс", "minecraft": "майнкрафт",
    "mineflayer": "майнфлеер", "paint": "пейнт", "tetris": "тетрис", "cs": "кс", "cs2": "кс два",
    "wi-fi": "вайфай", "wifi": "вайфай", "bluetooth": "блютус", "ssd": "эс эс дэ", "hdd": "жёсткий диск",
}

_EMOJI = re.compile("[\U0001F000-\U0001FAFF\U00002600-\U000027BF\U0001F900-\U0001F9FF⬀-⯿️‍]")
_URL = re.compile(r"(?:https?://|www\.)\S+", re.IGNORECASE)
_WORD = re.compile(r"[А-Яа-яЁё+́]+")

_lock = threading.Lock()
_acc = None
_custom: dict[str, str] = {}
_phrases: list[tuple[re.Pattern, str]] = []   # «в строю» → «в стро+ю»: ударение зависит от соседнего слова


def _plural(n: int, one: str, few: str, many: str) -> str:
    n = abs(n) % 100
    if 11 <= n <= 14:
        return many
    return {1: one, 2: few, 3: few, 4: few}.get(n % 10, many)


def _fem(n: int) -> str:
    """Число в женском роде: «одна целая», «две десятых»."""
    w = num2words(n, lang="ru")
    parts = w.split(" ")
    parts[-1] = {"один": "одна", "два": "две"}.get(parts[-1], parts[-1])
    return " ".join(parts)


def _num(s: str) -> str:
    s = s.replace(" ", "")
    if re.fullmatch(r"\d+[.,]\d+", s):
        whole, frac = re.split(r"[.,]", s)
        if len(frac) <= 3:
            return (f"{_fem(int(whole))} {_plural(int(whole), 'целая', 'целых', 'целых')} "
                    f"{_fem(int(frac))} "
                    f"{_plural(int(frac), *{1: ('десятая', 'десятых', 'десятых'), 2: ('сотая', 'сотых', 'сотых'), 3: ('тысячная', 'тысячных', 'тысячных')}[len(frac)])}")
        return f"{num2words(int(whole), lang='ru')} точка {' '.join(num2words(int(d), lang='ru') for d in frac)}"
    if len(s) > 15:   # номер карты, телефон — по цифрам
        return " ".join(num2words(int(d), lang="ru") for d in s)
    return num2words(int(s), lang="ru")


def _degrees(m: re.Match) -> str:
    sign = {"+": "плюс ", "-": "минус ", "−": "минус "}.get(m.group(1) or "", "")
    n = int(m.group(2))
    return f"{sign}{num2words(n, lang='ru')} {_plural(n, 'градус', 'градуса', 'градусов')}"


def _numbers(t: str) -> str:
    # версии и адреса «1.5.8», «192.168.0.1» — по частям через «точка»
    t = re.sub(r"(?<![\d.])\d+(?:\.\d+){2,}(?![\d.])", lambda m: " точка ".join(num2words(int(x), lang="ru") for x in m.group(0).split(".")), t)
    t = re.sub(r"(?<![\w])([+\-−])?(\d+)\s*°\s*[CС]?", _degrees, t)
    t = re.sub(r"(?<![\w\d])([+\-−])(\d+)(?!\d|[.,:]\d)", lambda m: ("плюс " if m.group(1) == "+" else "минус ") + m.group(2), t)
    t = re.sub(r"\b(\d{1,2}):(\d{2})\b",
               lambda m: f"{num2words(int(m.group(1)), lang='ru')} {num2words(int(m.group(2)), lang='ru') if m.group(2) != '00' else 'ноль ноль'}", t)
    t = re.sub(r"(\d+(?:[.,]\d+)?)\s*%",
               lambda m: f"{_num(m.group(1))} {'процента' if re.search(r'[.,]', m.group(1)) else _plural(int(m.group(1)), 'процент', 'процента', 'процентов')}", t)
    t = re.sub(r"\$\s*(\d+)|(\d+)\s*\$",
               lambda m: (lambda n: f"{_num(str(n))} {_plural(n, 'доллар', 'доллара', 'долларов')}")(int(m.group(1) or m.group(2))), t)
    t = re.sub(r"(\d+)\s*(?:₽|руб\.?(?=\s|$))",
               lambda m: f"{_num(m.group(1))} {_plural(int(m.group(1)), 'рубль', 'рубля', 'рублей')}", t)
    t = re.sub(r"(\d+)\s*€", lambda m: f"{_num(m.group(1))} евро", t)
    t = re.sub(r"(\d+)\s*[-–]\s*(й|я|е|го|му|м|х|ю|ой|ый|ая|ое)\b",
               lambda m: num2words(int(m.group(1)), lang="ru", to="ordinal"), t)
    t = re.sub(r"(\d)\s*\+\s*(\d)", r"\1 плюс \2", t)
    t = re.sub(r"(\d)\s*=\s*(\d)", r"\1 равно \2", t)
    t = re.sub(r"(\d)\s*[x×х]\s*(\d)", r"\1 на \2", t)
    t = re.sub(r"\d+(?:[.,]\d+)?", lambda m: _num(m.group(0)), t)
    return t


def _translit(t: str) -> str:
    for key in sorted((k for k in TRANSLIT if not re.fullmatch(r"[a-z0-9]+", k)), key=len, reverse=True):
        t = re.sub(rf"(?<![\w]){re.escape(key)}(?![\w+#])", TRANSLIT[key], t, flags=re.IGNORECASE)
    return re.sub(r"[A-Za-z][A-Za-z0-9]*", lambda m: TRANSLIT.get(m.group(0).lower(), m.group(0)), t)


def clean(text: str) -> str:
    """Всё, что Qwen прочитала бы вслух символами, — вон или словами."""
    t = unicodedata.normalize("NFC", text.replace(ACUTE, ""))
    t = _URL.sub(" ссылка ", t)
    t = _EMOJI.sub(" ", t)
    t = re.sub(r"\*\*|__|`+|~~", "", t)                  # markdown
    t = t.replace("№", " номер ").replace("&", " и ").replace("…", "...")
    t = _translit(t)
    t = _numbers(t)
    t = re.sub(r"(?<=\w)/(?=\w)", " или ", t)
    t = re.sub(r"[+*#_|~^<>\\/=@{}\[\]]", " ", t)        # «плюс», «решётка», «подчёркивание» вслух не нужны
    t = re.sub(r"\s*[-–—]{2,}\s*", " — ", t)
    t = re.sub(r"\s+", " ", t).strip()
    return t


def _load_custom() -> dict[str, str]:
    if not CUSTOM_FILE.exists():
        return {}
    try:
        raw = json.loads(CUSTOM_FILE.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        print(f"ru_text: {CUSTOM_FILE.name} — битый JSON ({e}), свой словарь пропущен", flush=True)
        return {}
    out = {}
    for k, v in raw.items():
        if k.startswith("_") or not isinstance(v, str):
            continue
        v = v.strip().lower()
        if v.replace("+", "").replace("ё", "е") != k.strip().lower().replace("ё", "е"):
            print(f"ru_text: «{k}» → «{v}» меняет само слово, а не ударение — пропускаю", flush=True)
            continue
        out[k.strip().lower()] = v
    return out


def _phrase_rules(custom: dict[str, str]) -> list[tuple[re.Pattern, str]]:
    """Записи словаря из нескольких слов: ищутся в размеченном тексте без учёта уже стоящих «+»."""
    rules = []
    for k, v in custom.items():
        if " " not in k:
            continue
        words = k.split()
        letter = lambda ch: "[её]" if ch in "её" else re.escape(ch)
        body = r"\s+".join(r"\+?".join(letter(ch) for ch in w) + r"\+?" for w in words)
        rules.append((re.compile(rf"(?<![А-Яа-яЁё+]){body}(?![А-Яа-яЁё])", re.IGNORECASE), v))
    return rules


def _apply_phrases(marked: str) -> str:
    def repl(m: re.Match, v: str) -> str:
        src = m.group(0).replace("+", "")
        out, i = [], 0
        for ch in v:
            if ch == "+":
                out.append("+")
                continue
            while i < len(src) and src[i].isspace() and not ch.isspace():
                out.append(src[i])
                i += 1
            if i < len(src):
                # буквы — из словаря (там может не быть ошибочной ё RUAccent), регистр — как в тексте
                out.append(ch if ch.isspace() else (ch.upper() if src[i].isupper() else ch))
                i += 1
        return "".join(out)
    for rx, v in _phrases:
        marked = rx.sub(lambda m, v=v: repl(m, v), marked)
    return marked


class CompactAccents:
    """
    Полный словарь ударений RUAccent (3,19 млн слов) без питоньих строк. Обычный dict из него держал ~2 ГБ ОЗУ (замер
    18.11: +2108 МиБ рабочего набора у голосового сервера). У 99,998 % слов значение — то же слово с одним «+», поэтому
    хранится: все слова одним блоком в cp1251 (отсортированы), смещения (uint32) и позиция «+» (uint8; 255 — без знака).
    ~50 МБ. Поиск — двоичный по блоку. Слова, что не ложатся в эту схему или в cp1251, — в маленьком обычном словаре.
    Сверху — overlay: свой словарь друга и буквы (как в RUAccent: они важнее полного словаря); pop/update — в overlay.
    RUAccent трогает словарь только через .get(слово, слово) (ruaccent.py, _process_accent).
    """

    NO_MARK = 255

    def __init__(self, blob, offsets, pos, odd: dict[str, str], overlay: dict[str, str]):
        self._blob = blob.tobytes() if hasattr(blob, "tobytes") else bytes(blob)
        self._off = offsets
        self._pos = pos
        self._odd = odd
        self.overlay = overlay
        self._n = len(pos)

    @staticmethod
    def build(full: dict[str, str]):
        import numpy as np
        rows, odd = [], {}
        for k, v in full.items():
            try:
                kb = k.encode("cp1251")
            except UnicodeEncodeError:
                odd[k] = v
                continue
            if v == k:
                rows.append((kb, CompactAccents.NO_MARK))
            elif v.count("+") == 1 and v.replace("+", "") == k and v.index("+") < 255:
                rows.append((kb, v.index("+")))
            else:
                odd[k] = v
        rows.sort(key=lambda r: r[0])
        blob = b"".join(r[0] for r in rows)
        offsets = np.zeros(len(rows) + 1, dtype=np.uint32)
        np.cumsum([len(r[0]) for r in rows], out=offsets[1:])
        pos = np.fromiter((r[1] for r in rows), dtype=np.uint8, count=len(rows))
        return np.frombuffer(blob, dtype=np.uint8), offsets, pos, odd

    def _base(self, word: str) -> str | None:
        if word in self._odd:
            return self._odd[word]
        try:
            q = word.encode("cp1251")
        except UnicodeEncodeError:
            return None
        lo, hi, blob, off = 0, self._n, self._blob, self._off
        while lo < hi:
            mid = (lo + hi) // 2
            key = blob[int(off[mid]):int(off[mid + 1])]
            if key < q:
                lo = mid + 1
            elif key > q:
                hi = mid
            else:
                p = int(self._pos[mid])
                return word if p == self.NO_MARK else word[:p] + "+" + word[p:]
        return None

    def get(self, word: str, default=None):
        if word in self.overlay:
            return self.overlay[word]
        v = self._base(word)
        return default if v is None else v

    def __contains__(self, word) -> bool:
        return word in self.overlay or self._base(word) is not None

    def __getitem__(self, word: str) -> str:
        v = self.get(word)
        if v is None:
            raise KeyError(word)
        return v

    def __len__(self) -> int:
        return self._n + len(self._odd) + len(self.overlay)

    def pop(self, word: str, default=None):
        return self.overlay.pop(word, default)

    def update(self, other) -> None:
        self.overlay.update(other)


def _compact_dictionary():
    """
    Сжатый полный словарь RUAccent: кэш models\\accents_compact.npz (собирается один раз из accents.json.gz за ~10 с,
    пересобирается, если словарь пакета сменился). Дальше грузится за доли секунды без 2 ГБ питоньих строк.
    """
    import gzip
    import os
    import numpy as np
    import ruaccent
    src = Path(ruaccent.__file__).resolve().parent / "dictionary" / "accents.json.gz"
    cache = ROOT.parent / "models" / "accents_compact.npz"
    stamp = f"{src.stat().st_size}:{int(src.stat().st_mtime)}"
    if cache.exists():
        try:
            with np.load(cache, allow_pickle=False) as z:
                if str(z["stamp"]) == stamp:
                    odd = json.loads(str(z["odd"]))
                    return z["blob"], z["offsets"], z["pos"], odd
        except (OSError, ValueError, KeyError):
            pass
    full = json.load(gzip.open(src, "rt", encoding="utf-8"))
    blob, offsets, pos, odd = CompactAccents.build(full)
    del full
    import gc
    gc.collect()
    cache.parent.mkdir(parents=True, exist_ok=True)
    tmp = cache.with_name(cache.stem + ".tmp.npz")
    np.savez(tmp, blob=blob, offsets=offsets, pos=pos, odd=np.array(json.dumps(odd, ensure_ascii=False)), stamp=np.array(stamp))
    os.replace(tmp, cache)
    return blob, offsets, pos, odd


def _skip_dead_rule_engine() -> None:
    """
    RUAccent (1.x) в load() поднимает движок правил — лемматизатор rulemma и POS-теггер rupostagger (+1,5 ГБ ОЗУ: замер
    18.11, лемматизатор +1397 МиБ из файла на 16 МБ), — но нигде его не вызывает: self.rule_accent встречается только в
    load. Его загрузка подменяется пустой — ответы те же (проверено на tts/stress_sentences.txt: 0 расхождений).
    Если в другой версии движок правил где-то вызывают, подмены нет: грузится как было.
    """
    import inspect
    import ruaccent.ruaccent as ra
    try:
        used = set(re.findall(r"self\.rule_accent\.(\w+)", inspect.getsource(ra)))
    except (OSError, TypeError):
        return
    if used != {"load"}:
        return
    from ruaccent import rule_accent_engine
    rule_accent_engine.RuleEngine.load = lambda self, path: None


def load(omograph_model: str = "turbo3.1") -> None:
    """
    RUAccent и свой словарь. Полный словарь ударений — сжатый (CompactAccents, ~50 МБ вместо ~2 ГБ): сам RUAccent
    грузится без него (use_dictionary=False тянет лишь маленький accents_nn), полный подставляется сверху — те же
    ответы, что с use_dictionary=True (свой словарь и буквы по-прежнему важнее полного).
    """
    global _acc, _custom, _phrases
    from ruaccent import RUAccent
    with _lock:
        _custom = _load_custom()
        _phrases = _phrase_rules(_custom)
        acc = RUAccent()
        custom = {k: v for k, v in _custom.items() if " " not in k}
        _skip_dead_rule_engine()
        acc.load(omograph_model_size=omograph_model, use_dictionary=False, custom_dict=custom, device="CPU")
        blob, offsets, pos, odd = _compact_dictionary()
        overlay = dict(custom)
        overlay.update(getattr(acc, "letters_accent", {}) or {})
        acc.accents = CompactAccents(blob, offsets, pos, odd, overlay)
        _acc = acc


def reload_custom() -> int:
    """Перечитать свой словарь без перезапуска (RUAccent держит его в своей таблице ударений)."""
    global _custom, _phrases
    with _lock:
        new = _load_custom()
        if _acc is not None:
            for k in _custom.keys() - new.keys():
                _acc.accents.pop(k, None)
            _acc.accents.update({k: v for k, v in new.items() if " " not in k})
        _custom = new
        _phrases = _phrase_rules(new)
        return len(new)


def _mark(word: str) -> str:
    """«з+амок» → «за́мок»; у односложных и слов с ё знак снимается."""
    plain = word.replace("+", "")
    vowels = sum(ch in VOWELS for ch in plain)
    if vowels < 2 or "ё" in plain.lower() or "+" not in word:
        return plain
    out, stress = [], False
    for ch in word:
        if ch == "+":
            stress = True
            continue
        out.append(ch)
        if stress and ch in VOWELS:
            out.append(ACUTE)
            stress = False
    return "".join(out)


def prepare(text: str) -> tuple[str, str]:
    """(текст для модели со знаками U+0301, тот же текст с «+» — для журнала)."""
    t = clean(text)
    if not t or _acc is None:
        return t, t
    with _lock:
        try:
            marked = _acc.process_all(t)
        except Exception as e:   # акцентуатор споткнулся о редкий символ — лучше без ударений, чем без фразы
            print(f"ru_text: RUAccent не справился ({e!r}) — фраза без знаков", flush=True)
            return t, t
    # свой словарь поверх: RUAccent сперва решает омографы своей моделью, и слово с уже поставленным ударением
    # (например «п+оняла» вместо «понял+а») в таблицу ударений, где лежит свой словарь, уже не заглядывает
    marked = _apply_phrases(_WORD.sub(_override, marked))
    # RUAccent вычищает из текста всё, кроме букв и обычной пунктуации, — пунктуация и регистр у него те же
    return _WORD.sub(lambda m: _mark(m.group(0)), marked), marked


def accent(text: str) -> str:
    """Ударения для показа человеку (тренажёр чтения): текст как есть, без замены чисел и символов,
    ударная гласная — после «+», буквы ё восстановлены RUAccent. Без акцентуатора — текст без знаков."""
    if _acc is None or not text.strip():
        return text
    with _lock:
        marked = _acc.process_all(text)
    return _apply_phrases(_WORD.sub(_override, marked))


def _override(m: re.Match) -> str:
    word = m.group(0)
    plain = word.replace("+", "")
    low = plain.lower()
    mine = _custom.get(low) or _custom.get(low.replace("ё", "е"))
    if mine is None or len(mine.replace("+", "")) != len(plain):
        return word
    # буквы и «+» — из своего словаря (RUAccent мог ошибочно поставить ё), регистр — как в тексте
    out, i = [], 0
    for ch in mine:
        if ch == "+":
            out.append("+")
            continue
        out.append(ch.upper() if plain[i].isupper() else ch)
        i += 1
    return "".join(out)
