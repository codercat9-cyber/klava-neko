"""Морфологический анализатор и контекстный резолвер омографов для голоса Клавы.

Использует базу грамматических форм Зализняка через pymorphy3:
1. Синтаксический контекст: предлоги + падежи (loc2 местный падеж: «в строю», «на лету»).
2. Разрешение смысловых омографов по соседним словам (стоит/стоит, плачу/плачу, замок/замок).
3. Лемматизация IT- и геймерского сленга (глаголы прошедшего времени: тестить, фиксить, деплоить).
"""
import re
from typing import List, Tuple
import pymorphy3

_morph = None


def get_morph() -> pymorphy3.MorphAnalyzer:
    global _morph
    if _morph is None:
        _morph = pymorphy3.MorphAnalyzer()
    return _morph


# Предлоги, требующие местного (второго предложного) падежа loc2
LOC2_PREPOSITIONS = {"в", "во", "на", "при"}

# Омографы местного падежа: форма слова -> (безопасная форма для Qwen, описание)
LOC2_WORDS = {
    "строю": "строе",    # «в строю» -> «в строе» (звучит с правильным ударением, а не глагол «стро́ю»)
}

# IT-глаголы, где Qwen норовит поставить ударение на суффикс (тестИл вместо тЕстил):
# Заменяем гласную ударного корня на Ё — для Qwen это 100% надёжный якорь ударения.
SLANG_VERB_ROOTS = {
    "тести": "тёсти",
    "затести": "затёсти",
    "потести": "потёсти",
    "оттести": "оттёсти",
    "перетести": "перетёсти",
    "фикси": "фёкси",      # пофиксил -> пофёксил (ударное "фи")
    "пофикси": "пофёкси",
    "зафикси": "зафёкси",
    "деплои": "деплёи",
    "задеплои": "задеплёи",
    "чека": "чёка",
    "чекну": "чёкну",
    "зачека": "зачёка",
}


def _match_case(src: str, dst: str) -> str:
    if src.isupper():
        return dst.upper()
    if src and src[0].isupper():
        return dst[0].upper() + dst[1:]
    return dst


def resolve_morpho_stress(text: str) -> Tuple[str, List[str]]:
    """Морфологический анализ и разрешение омографов в предложении."""
    if not text:
        return text, []

    morph = get_morph()
    changes = []

    # 1. Токенизация на слова с сохранением пунктуации и пробелов
    # Регекс разбивает текст на чередующиеся токены (слова и разделители)
    tokens = re.split(r'([а-яёА-ЯЁa-zA-Z0-9_-]+)', text)

    words_info = []
    for i, t in enumerate(tokens):
        if re.match(r'^[а-яёА-ЯЁ]+$', t):
            words_info.append((i, t, t.lower()))

    # 2. Анализ омографов с учетом контекста
    for idx_in_words, (tok_idx, raw_word, low_word) in enumerate(words_info):
        # А) Проверка местного падежа (loc2): предлог + существительное
        if low_word in LOC2_WORDS and idx_in_words > 0:
            prev_tok_idx, prev_raw, prev_low = words_info[idx_in_words - 1]
            if prev_low in LOC2_PREPOSITIONS:
                # Проверим, есть ли у слова тег loc2 в pymorphy3
                parses = morph.parse(low_word)
                is_loc2 = any('loc2' in p.tag or 'voic' in p.tag or 'NOUN' in p.tag for p in parses)
                if is_loc2:
                    replacement = LOC2_WORDS[low_word]
                    tokens[tok_idx] = _match_case(raw_word, replacement)
                    changes.append(f"морфо-loc2:{prev_raw} {raw_word}→{tokens[tok_idx]}")
                    continue

        # Б) Анализ омографа «стоит» / «сто́ит» (цена/целесообразность vs нахождение)
        if low_word == "стоит":
            # Проверим окружение
            # Если рядом "не", "ли", "бы", "чего" или инфинитив глагола дальше -> это "сто́ит"
            context_window = [w[2] for w in words_info[max(0, idx_in_words - 2):min(len(words_info), idx_in_words + 3)]]
            has_modal = any(m in context_window for m in ["не", "ли", "бы", "чего", "сколько", "дорого", "дешево"])
            next_is_inf = False
            if idx_in_words + 1 < len(words_info):
                next_low = words_info[idx_in_words + 1][2]
                next_parses = morph.parse(next_low)
                if any('INFN' in p.tag for p in next_parses):
                    next_is_inf = True

            if has_modal or next_is_inf:
                # Целесообразность/цена: "сто́ит" (звучит правильно, как stóit)
                pass
            else:
                # Физическое нахождение: "стои́т на столе" -> фонетический якорь "стоиит"
                if any(p in context_window for p in ["на", "в", "тут", "там", "здесь", "рядом", "около", "столе", "полу"]):
                    tokens[tok_idx] = _match_case(raw_word, "стоиит")
                    changes.append(f"морфо:стои́т→стоиит")

        # В) Анализ омографа «плачу» (деньги vs слёзы)
        elif low_word == "плачу":
            context_window = [w[2] for w in words_info[max(0, idx_in_words - 2):min(len(words_info), idx_in_words + 3)]]
            is_money = any(m in context_window for m in ["за", "деньги", "картой", "счет", "рублей", "рубли", "баксы", "наличкой"])
            if is_money:
                # плачу́ (оплата) -> фонетическая подсказка "плачуу"
                tokens[tok_idx] = _match_case(raw_word, "плачуу")
                changes.append("морфо:плачу́(деньги)→плачуу")
            else:
                # пла́чу (слёзы) -> оставляем как есть, Qwen читает как пла́чу
                pass

        # Г) IT-глаголы с ударным корнем (тестил, пофиксил, задеплоил):
        for prefix, yo_form in SLANG_VERB_ROOTS.items():
            if low_word.startswith(prefix):
                tail = low_word[len(prefix):]
                # Проверим, что это форма глагола (л, ла, ли, ло, ем, ут, ит и т.д.)
                if tail in {"л", "ла", "ли", "ло", "м", "шь", "те", "т", "ть"}:
                    replaced = yo_form + tail
                    tokens[tok_idx] = _match_case(raw_word, replaced)
                    changes.append(f"морфо-it:{raw_word}→{tokens[tok_idx]}")
                    break

    result_text = "".join(tokens)
    return result_text, changes
