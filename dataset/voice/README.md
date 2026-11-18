# Датасет голоса Клавы

Готовые обучающие наборы, на которых училась эмоциональная LoRA её голоса (`models\emo_lora`, база —
`Qwen3-TTS-12Hz-1.7B-Base-ru-stress`). Самой модели и адаптера здесь нет — только данные.

## Что лежит

`emotion\` — строки JSONL, по одной фразе: `text` (с ударениями U+0301, как их ставит сервер голоса), `style`
(эмоция Клавы), `instruct` (указание манеры — та же строка, что в `tts\emotions.json`), `codes` (коды кодека
Qwen3-TTS-Tokenizer-12Hz, 12 кадров/с — по ним и идёт обучение), `audio`/`ref` (пути к исходным wav у автора; самих wav
в пакете нет — их можно пересобрать скриптами из исходных корпусов).

| Файл | Откуда | Что |
|---|---|---|
| `train.jsonl`, `val.jsonl` | RESD (Aniemore, русская эмоциональная речь, 7 эмоций) + шёпот из LPC | `tts\emo_train\prepare.py` |
| `train_dialogs.jsonl`, `val_dialogs.jsonl` | Dialogs (langswap, Interspeech 2026, OpenRAIL): женские голоса Маша и Света, 12 манер | `tts\emo_train\prepare_dialogs.py` |
| `train_nv.jsonl`, `val_nv.jsonl` | NonverbalTTS: смех в речи | `tts\emo_train\prepare_nvtts.py` |

Обучение — `tts\emo_train\train.py` (`--extra nv,dialogs`), слияние и проверка — `merge.py`, `evaluate.py`.
Её собственный голос (эталон `tts\voices\candidates\klava_02.wav`, эталоны эмоций, смех, живые звуки) — в `tts\voices`.

## Чего здесь нет и не будет

- Записей голосов друзей (`data\speakers`: голосовые профили для узнавания, кто говорит) — это чужие голоса, они
  остаются только на машине автора.
- Исходных wav корпусов — их лицензии разрешают брать с источника, а не раздавать копии.

Перед тем как делать репозиторий публичным — проверь лицензии RESD, Dialogs и NonverbalTTS.
