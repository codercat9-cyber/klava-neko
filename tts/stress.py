"""Нормализация русского текста для TTS (Silero и Qwen).

Порядок обработки:
  1. Транслит англицизмов/аббревиатур в русскую фонетику (GPT → джипити, USB → юэсби).
  2. Числа → слова (1024 → тысяча двадцать четыре, 25% → двадцать пять процентов).
  3. Пользовательский словарь ударений (custom_stress.json) — слова, где RUAccent мажет
     (пример: «тестил» → «тест+ил»). Можно править файл руками.
  4. Ёфикация и омографы через RUAccent (turbo2, ONNX).

Для Silero: возвращает текст с плюсиками (`+гласная`) — движок их понимает нативно.
Для Qwen3-TTS: плюсики модель читает как символ, поэтому вызывай `apply_stress_qwen()` — она снимает плюсы,
оставляя только Ё-маркеры и словарные замены. Ё в Qwen — единственный надёжный якорь ударения.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

from ruaccent import RUAccent
from morpho_stress import resolve_morpho_stress

try:
    from num2words import num2words as _num2words

    def _n2w(n: int) -> str:
        return _num2words(n, lang="ru")
except ImportError:
    def _n2w(n: int) -> str:
        return str(n)


ACUTE = "́"
ROOT = Path(__file__).resolve().parent
CUSTOM_STRESS_FILE = ROOT / "custom_stress.json"

# Ё-словарь: где RUAccent не знает разговорную/матерную форму, — Ё как ударный слог + нормативные замены сленга.
# Для Qwen3-TTS Ё — единственный работающий якорь ударения (плюсы движок читает как символ).
CUSTOM_DICT: dict[str, str] = {
    "здарова": "здорово", "здарово": "здорово", "здоров": "здорово",
    "щас": "сейчас", "ща": "сейчас", "щаз": "сейчас",
    "чё": "чего", "чо": "чего",
    "чёт": "чего-то", "чёто": "чего-то", "чото": "чего-то",
    "нихрена": "ни хрена", "нихуя": "ни хуя",
    "нахер": "на хер", "нафиг": "на фиг",
    "тыщ": "тысяч", "тыща": "тысяча", "тыщу": "тысячу",
    # Разговорное «пивко» Qwen читает с неправильным ударением («пИвка» вместо «пивка́») —
    # заменяем на нормативную форму «пиво», модель её знает и произносит правильно.
    "пивко": "пиво", "пивка": "пива", "пивке": "пиве", "пивку": "пиву", "пивком": "пивом",
    "винишко": "вино", "винишка": "вина", "коньячок": "коньяк", "коньячку": "коньяку",
    "кофеек": "кофе", "кофейку": "кофе", "чаёк": "чай", "чайку": "чаю",
    # Ё-подмены для прошедшего времени (ударение на первый слог) — Qwen без этого читает по-своему («тестИл»)
    "тестил": "тёстил", "тестила": "тёстила", "тестили": "тёстили", "тестило": "тёстило",
    "затестил": "затёстил", "затестила": "затёстила", "затестили": "затёстили",
    "потестил": "потёстил", "потестила": "потёстила", "потестили": "потёстили",
    "перетестил": "перетёстил", "перетестила": "перетёстила",
    "оттестил": "оттёстил", "оттестила": "оттёстила",
    "чекнул": "чёкнул", "чекнула": "чёкнула", "чекнули": "чёкнули",
    # Клавины формы имени — RUAccent их не всегда узнаёт
    "клавка": "клавка", "клавой": "клавой", "клавы": "клавы",
    # Мат: Ё ставим ТОЛЬКО где ударение реально на первый слог (междометия, ёбнутый, ёбало).
    # Формы с ударением НЕ на «е» («ебать» - на «а», «заебись» - на «и», «заебал» - на «а») тут не трогаем:
    # ставить туда Ё - это переносить ударение и звучит криво (Клава говорила «заЁбись» вместо «заеби́сь»).
    "епта": "ёпта", "ебта": "ёбта",
    "ебнулся": "ёбнулся", "ебнулась": "ёбнулась", "ебнулись": "ёбнулись", "ебнуться": "ёбнуться",
    "ебнутый": "ёбнутый", "ебнутая": "ёбнутая", "ебнутое": "ёбнутое",
    "ебало": "ёбало", "ебальник": "ёбальник",
    "ебнул": "ёбнул", "ебнула": "ёбнула",
    # киберсленг с Ё
    "клева": "клёва", "клевый": "клёвый", "клевая": "клёвая", "клевое": "клёвое", "клевые": "клёвые",
    "черт": "чёрт", "черта": "чёрта", "черту": "чёрту",
    "четкий": "чёткий", "четкая": "чёткая", "четко": "чётко",
    "жестко": "жёстко", "жесткий": "жёсткий", "жесткая": "жёсткая",
    "легкий": "лёгкий", "легкая": "лёгкая",
    "теплый": "тёплый", "теплая": "тёплая", "теплое": "тёплое",
    "тертый": "тёртый",
    "стеб": "стёб", "стебный": "стёбный",
    "надежный": "надёжный", "надежная": "надёжная",
}

# Транслит: англицизмы → русская фонетика (Qwen/Silero читают латиницу по буквам, отсюда «эф-эл-а-эс-эйч»).
TRANSLIT: dict[str, str] = {
    "flashnext": "флэшнэкст", "flash-next": "флэшнэкст",
    "deepseek": "дипсик", "deep-seek": "дипсик",
    "antigravity": "антигравити",
    "gemini": "джемини", "gpt": "джипити", "gpt4": "джипити четыре", "gpt5": "джипити пять",
    "claude": "клод", "claude code": "клод код",
    "qwen": "квэн", "qwen3": "квэн три", "qwen3-vl": "квэн три вэ-эл",
    "sonnet": "соннет", "opus": "опус", "haiku": "хайку",
    "llama": "лама", "llama.cpp": "лама си-пи-пи",
    "whisper": "уиспер", "whisper.cpp": "уиспер си-пи-пи",
    "ollama": "олама",
    "cuda": "куда", "vram": "вирам", "gpu": "джипию", "cpu": "сипию",
    "llm": "элэлэм", "vad": "вад", "stt": "эстэтэ", "tts": "тэтээс",
    "api": "апи", "sdk": "эсдэка", "cli": "сиэлай",
    "http": "эйч-ти-ти-пи", "https": "эйч-ти-ти-пи-эс", "url": "юэрэл",
    "json": "джейсон", "csv": "си-эс-ви", "xml": "иксэмэл", "yaml": "ямл",
    "html": "эйчтэмэл", "css": "си-эс-эс", "js": "джиэс",
    "usb": "юэсби", "hdmi": "эйч-ди-эм-ай", "rgb": "эрджиби",
    "png": "пиэнджи", "jpg": "джипег", "jpeg": "джипег", "gif": "гиф", "svg": "эсвиджи",
    "pdf": "пидиэф", "exe": "экзэ", "dll": "диэлэл",
    "wav": "вав", "mp3": "эмпэ три", "mp4": "эмпэ четыре",
    "vpn": "вэпээн", "dns": "диэнэс", "ip": "айпи",
    "ai": "эйай",
    "ok": "окей", "okay": "окей",
    "windows": "виндоус", "linux": "линукс", "macos": "макос",
    "github": "гитхаб", "gitlab": "гитлаб", "docker": "докер",
    "python": "пайтон", "nodejs": "ноудджиэс", "npm": "эн-пи-эм",
    "dotnet": "дотнэт", ".net": "дотнэт",
    "vs code": "вээс код", "vscode": "вээс код",
    "webview2": "вэбвью два", "webview": "вэбвью",
    "youtube": "ютьюб", "twitch": "твич", "discord": "дискорд",
    "spotify": "спотифай", "telegram": "телеграм",
    "microsoft": "майкрософт", "google": "гугл", "openai": "опэн-эй-ай",
    "nvidia": "энвидиа", "amd": "а-эм-ди", "intel": "интел", "rtx": "эр-ти-икс",
}


def _load_custom_stress() -> dict[str, str]:
    """Пользовательские ударения из tts/custom_stress.json. Служебные ключи (`_*`) пропускаем."""
    if not CUSTOM_STRESS_FILE.exists():
        return {}
    try:
        raw = json.loads(CUSTOM_STRESS_FILE.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        print(f"stress: {CUSTOM_STRESS_FILE.name} невалидный JSON: {e}")
        return {}
    return {k.lower(): v for k, v in raw.items() if not k.startswith("_")}


_custom_stress: dict[str, str] = _load_custom_stress()
_accentizer: RUAccent | None = None
_stressrnn = None


def get_stressrnn():
    """Ленивая инициализация StressRNN (BiLSTM для расстановки ударений). None — если пакета нет."""
    global _stressrnn
    if _stressrnn is None:
        try:
            import stressrnn
            _stressrnn = stressrnn.StressRNN()
        except Exception:
            _stressrnn = False
    return _stressrnn if _stressrnn else None


def _apply_stressrnn_yofy(text: str, changes: list[str]) -> str:
    """StressRNN даёт «те+стил» — превращаем «+е» в «ё» (для Qwen: Ё — единственный ударный якорь).
    Слова где RUAccent+eyo уже поставили Ё — не трогаем. Плюсы на других гласных снимаем."""
    m = get_stressrnn()
    if m is None:
        return text
    try:
        with _lock_rnn:
            stressed = m.put_stress(text)
    except Exception:
        return text
    # Пословно: если в слове уже есть Ё — оставляем без вмешательства (уже правильно).
    def word_repl(mo: re.Match) -> str:
        w = mo.group(0)
        # Если слово уже с Ё — берём оригинал без плюсов
        if "ё" in w.lower():
            return re.sub(r"\+", "", w)
        # +е → ё, регистр учитываем
        w = w.replace("+е", "ё").replace("+Е", "Ё")
        # плюсы на других гласных — просто убираем (Qwen не поймёт)
        w = re.sub(r"\+", "", w)
        return w
    result = re.sub(r"[а-яёА-ЯЁ+]+", word_repl, stressed)
    if result != text: changes.append("stressrnn→ё")
    return result


import threading as _threading
_lock_rnn = _threading.Lock()


def _expand_yo_pattern(line: str) -> list[str]:
    """Распаковать строку словаря eyo-kernel: «Аксён(|а|ам|у|ы)» → ['Аксён','Аксёна','Аксёнам','Аксёну','Аксёны']."""
    line = line.strip()
    if not line:
        return []
    if "(" not in line:
        return [line]
    base, tail = line.split("(", 1)
    endings = tail.rstrip(")").split("|")
    return [base + e for e in endings]


def _load_yo_dict() -> dict[str, str]:
    """Загрузить eyo-kernel/dict/safe.txt: ~14k шаблонов → ~150k форм. Ключ — форма БЕЗ Ё (в нижнем регистре)."""
    path = ROOT / "dicts" / "yo_safe.txt"
    if not path.exists():
        return {}
    mapping: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        for form in _expand_yo_pattern(line):
            if "ё" not in form and "Ё" not in form:
                continue
            key = form.replace("ё", "е").replace("Ё", "Е").lower()
            mapping[key] = form
    return mapping


_yo_dict: dict[str, str] = _load_yo_dict()

# Фразовые замены: пары «искать → заменить» для слов, которые вне контекста менять нельзя (омографы
# по позиции ударения). Пример: «строю» само по себе — это может быть глагол «я стро́ю дом» (правильно
# читается), но «в строю» — существительное «строю́» (Qwen читает «стро́ю», неправильно). Заменяем только
# фразу «в строю» → «в строе» (литературная форма, Qwen звучит правильно).
PHRASE_REPLACES: dict[str, str] = {
    r"\bв строю\b": "в строе",
    r"\bна строю\b": "на строе",
}

# Слова, где RUAccent ставит Ё для менее употребительного омографа, — принудительно возвращаем в «е»-форму.
# «нёбо» — часть рта, редко в разговоре; «небо» — куда чаще. Аналогично «все»/«всё» RUAccent разрешает
# по контексту, а вот эти капканы (медицинские/анатомические Ё) в разговоре только мешают.
YO_DEYOFY: set[str] = {
    "нёбо", "нёба", "нёбу", "нёбе", "нёбом", "нёбах", "нёбам", "нёбами",
    # можно точечно расширять — сюда добавляй слова, где Клава говорит с Ё, а должна с Е
}


def _deyofy(text: str) -> str:
    """Обратная ёфикация для узкого списка омографов (например, "нёбе"→"небе"): защита от RUAccent."""
    def repl(m: re.Match) -> str:
        raw = m.group(0)
        low = raw.replace("+", "").lower()
        if low in YO_DEYOFY:
            fixed = raw.replace("ё", "е").replace("Ё", "Е")
            return fixed
        return raw

    return re.sub(r"[а-яёА-ЯЁ+]+", repl, text)


def _apply_yo_dict(text: str, changes: list[str]) -> str:
    """Гарантированный ёфикатор из большого словаря — добивает всё что RUAccent пропустил.

    Матчит слово ЦЕЛИКОМ, включая плюсы RUAccent посреди («звезд+а»), но ключ ищет БЕЗ плюсов, — иначе
    regex бил слово на «звезд»+«а» и словарная форма «звезд» ошибочно триггерила замену на «звёзд».
    """
    if not _yo_dict:
        return text

    def repl(m: re.Match) -> str:
        raw = m.group(0)
        if "ё" in raw or "Ё" in raw:   # уже с Ё — не трогаем
            return raw
        raw_clean = raw.replace("+", "")
        key = raw_clean.lower()
        if key in _yo_dict:
            out = _match_case(raw_clean, _yo_dict[key])
            if out != raw_clean:
                changes.append(f"ё:{raw_clean}→{out}")
            return out
        return raw

    return re.sub(r"[а-яёА-ЯЁ+]+", repl, text)


def get_accentizer() -> RUAccent:
    global _accentizer
    if _accentizer is None:
        acc = RUAccent()
        acc.load(omograph_model_size="turbo2", device="CPU")
        _accentizer = acc
    return _accentizer


def reload_custom_stress() -> int:
    """Перечитать custom_stress.json без рестарта сервера. Возвращает количество слов в словаре."""
    global _custom_stress
    _custom_stress = _load_custom_stress()
    return len(_custom_stress)


def _match_case(src: str, dst: str) -> str:
    if src.isupper():
        return dst.upper()
    if src and src[0].isupper():
        return dst[0].upper() + dst[1:]
    return dst


def _percent_repl(m: re.Match) -> str:
    return f"{_n2w(int(m.group(1)))} процентов"


def _num_repl(m: re.Match) -> str:
    s = m.group(0)
    try:
        if "," in s or "." in s:
            return _num2words(float(s.replace(",", ".")), lang="ru")
        return _num2words(int(s), lang="ru")
    except (ValueError, NameError):
        return s


def _expand_numbers(text: str) -> str:
    text = re.sub(r"(\d+)\s*%", _percent_repl, text)
    text = re.sub(r"\b\d+([.,]\d+)?\b", _num_repl, text)
    return text


def _apply_phrase_replaces(text: str, changes: list[str]) -> str:
    """Контекстные фразовые замены до RUAccent (например, «в строю» → «в строе»)."""
    for pattern, repl in PHRASE_REPLACES.items():
        new_text = re.sub(pattern, repl, text, flags=re.IGNORECASE)
        if new_text != text:
            changes.append(f"фраза:{pattern}→{repl}")
            text = new_text
    return text


def _transliterate(text: str) -> str:
    """Заменить известные англицизмы на русскую запись (без учёта регистра, только целые слова)."""
    def repl(m: re.Match) -> str:
        w = m.group(0).lower()
        return TRANSLIT.get(w, m.group(0))

    for key in sorted(TRANSLIT.keys(), key=len, reverse=True):
        if " " in key or "-" in key or "." in key:
            text = re.sub(rf"\b{re.escape(key)}\b", TRANSLIT[key], text, flags=re.IGNORECASE)
    text = re.sub(r"[A-Za-z][A-Za-z0-9]*", repl, text)
    return text


def _strip_stress_marks(text: str) -> str:
    """Убрать все маркеры ударений: и combining acute, и плюсы. Нужно для Qwen3-TTS (он их не понимает)."""
    text = text.replace(ACUTE, "")
    text = re.sub(r"(?<![0-9])\+(?![0-9])", "", text)
    return text


def _apply_user_stress(text: str, changes: list[str]) -> str:
    """Подставить `+гласная` из пользовательского словаря (custom_stress.json) до RUAccent."""
    if not _custom_stress:
        return text

    def repl(m: re.Match) -> str:
        raw = m.group(0)
        low = raw.lower()
        if low in _custom_stress:
            out = _match_case(raw, _custom_stress[low])
            if out != raw:
                changes.append(f"user:{raw}→{out}")
            return out
        return raw

    # Слово — только буквы (без плюсов), чтобы не задеть уже расставленные ударения.
    return re.sub(r"[а-яёА-ЯЁ]+", repl, text)


def _apply_dict(text: str, changes: list[str]) -> str:
    """Ёфикация и нормативные замены сленга (CUSTOM_DICT)."""
    def repl(m: re.Match) -> str:
        raw = m.group(0)
        low = raw.lower()
        if low in CUSTOM_DICT:
            out = _match_case(raw, CUSTOM_DICT[low])
            if out != raw:
                changes.append(f"{raw}→{out}")
            return out
        return raw

    return re.sub(r"[а-яёА-ЯЁ]+", repl, text)


def apply_stress(text: str) -> tuple[str, list[str]]:
    """Полная обработка для Silero: возвращает текст с `+гласная` — движок использует их для ударения.

    Порядок: транслит → числа → CUSTOM_DICT (Ё/сленг) → пользовательские ударения (custom_stress.json) → RUAccent.
    CUSTOM_DICT идёт ПЕРЕД user_stress, чтоб Ё-замены не съедались словарём плюсов.
    """
    if not text:
        return text, []

    changes: list[str] = []

    # 0. Морфологический анализ контекста и омографов (pymorphy3 + словарь Зализняка)
    text, morpho_changes = resolve_morpho_stress(text)
    changes.extend(morpho_changes)

    text = _apply_phrase_replaces(text, changes)

    trans = _transliterate(text)
    if trans != text:
        changes.append("транслит")

    pre = _expand_numbers(trans)
    if pre != trans:
        changes.append("числа")

    pre = _apply_dict(pre, changes)
    pre = _apply_user_stress(pre, changes)

    # RUAccent проставляет `+` перед ударной гласной; своё пользовательское ударение он не тронет.
    try:
        acc = get_accentizer()
        ru_result = acc.process_all(pre)
    except Exception:
        ru_result = pre

    # Полноценный ёфикатор из eyo-kernel: 150k форм. Добивает всё что RUAccent пропустил,
    # особенно имена собственные (Ёжиков, Артём, Алёна) и редкие формы глаголов.
    ru_result = _apply_yo_dict(ru_result, changes)
    # И обратно снимаем Ё там, где RUAccent взял неправильный омограф (нёбо ≠ небо).
    ru_result = _deyofy(ru_result)

    # combining acute от старых кэшей RUAccent → тоже в формат `+гласная`
    ru_result = re.sub(rf"([аеёиоуыэюяАЕЁИОУЫЭЮЯ]){re.escape(ACUTE)}", r"+\1", ru_result)
    # хвостовые плюсы и двойные — нормализуем
    ru_result = re.sub(r"([аеёиоуыэюяАЕЁИОУЫЭЮЯ])\+", r"+\1", ru_result)
    ru_result = re.sub(r"\+{2,}", "+", ru_result)
    # плюсы, оторванные от гласной, — вон
    ru_result = re.sub(r"\+(?![аеёиоуыэюяАЕЁИОУЫЭЮЯ])", "", ru_result)

    return ru_result, changes


def apply_stress_qwen(text: str) -> tuple[str, list[str]]:
    """Для Qwen3-TTS: базовая ёфикация + StressRNN-BiLSTM для дополнительной автоматической подстановки Ё в
    ударных слогах с «е». Плюсы снимаются (Qwen их читает как символ). Ё в Qwen — единственный ударный якорь."""
    result, changes = apply_stress(text)
    # StressRNN уточняет ударения (BiLSTM, обученная на большом корпусе) — заполняет пробелы RUAccent+eyo.
    result = _apply_stressrnn_yofy(result, changes)
    # На всякий случай финально снимаем оставшиеся маркеры.
    return _strip_stress_marks(result), changes
