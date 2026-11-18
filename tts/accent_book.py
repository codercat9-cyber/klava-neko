"""Ударения для тренажёра чтения, когда голосовой сервер не запущен: разовый процесс с той же нейросетью RUAccent.

    stdin  {"texts": ["...", "..."]}   (UTF-8 JSON)
    stdout {"texts": ["...", "..."]}   ударная гласная — после «+», ё восстановлены

KlavaDesk (ReadingBook.cs) сначала спрашивает POST /accent у сервера голоса (там RUAccent уже загружен),
и только если сервера нет — запускает этот скрипт (~12 с на загрузку модели).
"""
import json
import sys

import ruaccent  # noqa: F401  (импорт в главном потоке, как в server.py)
import transformers  # noqa: F401

import ru_text


def main() -> int:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    req = json.loads(sys.stdin.read() or "{}")
    texts = [str(t) for t in req.get("texts", [])]
    ru_text.load()
    out = [ru_text.accent(t) for t in texts]
    sys.stdout.write(json.dumps({"texts": out}, ensure_ascii=False))
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
