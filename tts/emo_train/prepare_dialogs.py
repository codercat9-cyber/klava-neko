"""Русские актёрские эмоции для LoRA голоса Клавы (нейросеть, одноразовая подготовка).

Корпус Dialogs (langswap, Interspeech 2026): 20,6 ч студийной записи, театральные актёры играют диалоги, 12 манер,
лицензия OpenRAIL. Берутся только женские голоса — Маша (9,9 ч) и Света (4,4 ч). Друг 29.09: «потренировать русские
эмоции — может, есть банки школы актёрского мастерства».

Манера корпуса → стиль Клавы (указание манеры — ровно та строка, что сервер голоса подаёт как instruct, tts/emotions.json):
happy→happy, surprise→surprised, sad→sad, angry→angry, disgust→annoyed, arrogance→sly, fear→scared, whisper→whisper,
laughing→laugh (речь сквозь смех), neutral→calm (без указания). Стихи и скороговорки — мимо.
Ударения — из корпуса (разметка людьми, «+» перед ударной) → знак U+0301 после гласной, как их ставит сервер
(ru_text._mark: в односложных и словах с ё знака нет) — модель учится на том же виде текста, что получает вживую.
Эталон голоса (x-vector) — ДРУГАЯ, нейтральная фраза той же актрисы: эмоция должна браться из указания, не из отпечатка.

    tts\\.venv\\Scripts\\python.exe emo_train\\prepare_dialogs.py
Итог: data/emotion/dlg24/*.wav, data/emotion/train_dialogs.jsonl и val_dialogs.jsonl (для train.py --extra dialogs).
"""
from __future__ import annotations

import csv
import json
import random
import sys
from collections import defaultdict
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
import torch

TTS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TTS))
import ru_text  # noqa: E402

ROOT = TTS.parent
SRC = ROOT / "data" / "emotion" / "dialogs"
OUT = ROOT / "data" / "emotion"
WAVS = OUT / "dlg24"
TOKENIZER = ROOT / "models" / "Qwen3-TTS-Tokenizer-12Hz"
SR = 24000
MAP = {"happy": "happy", "surprise": "surprised", "sad": "sad", "angry": "angry", "disgust": "annoyed",
       "arrogance": "sly", "fear": "scared", "whisper": "whisper", "laughing": "laugh", "neutral": "calm"}
# сколько брать из самых больших манер (остальные — все): баланс, чтобы радость и спокойствие не задавили редкие
CAP = {"happy": 900, "calm": 600}


def instructs() -> dict[str, str]:
    e = json.loads((TTS / "emotions.json").read_text(encoding="utf-8"))
    styles = e.get("styles", e)
    out = {k: v.get("instruct", "") for k, v in styles.items() if isinstance(v, dict)}
    out["calm"] = ""
    return out


def marked(accent_text: str) -> str:
    """«Прив+ет, М+аша!» → «Приве́т, Ма́ша!» (как ru_text.prepare для сервера)."""
    return ru_text._WORD.sub(lambda m: ru_text._mark(m.group(0)), accent_text.replace("  ", " ").strip())


def to24k(path: Path) -> np.ndarray | None:
    wav, sr = sf.read(path, dtype="float32", always_2d=False)
    if wav.ndim > 1:
        wav = wav.mean(axis=1)
    wav = librosa.resample(wav, orig_sr=sr, target_sr=SR)
    wav, _ = librosa.effects.trim(wav, top_db=35)
    if not (0.8 <= len(wav) / SR <= 12.5):
        return None
    peak = float(np.max(np.abs(wav)))
    return (wav / peak * 0.9).astype(np.float32) if peak > 0 else None


def main() -> int:
    ins = instructs()
    missing = [s for s in set(MAP.values()) if s not in ins]
    if missing:
        print(f"нет стилей в emotions.json: {missing}")
        return 1
    WAVS.mkdir(parents=True, exist_ok=True)
    rows = []
    for split, name in (("train", "train.csv"), ("val", "val.csv"), ("val", "test.csv")):
        with open(SRC / name, encoding="utf-8") as f:
            for r in csv.DictReader(f, delimiter="|"):
                if r["speaker_id"] in ("M", "S") and r["emotion"] in MAP and (SRC / r["audio_path"]).exists():
                    r["split"] = split
                    r["style"] = MAP[r["emotion"]]
                    rows.append(r)
    rnd = random.Random(29)
    rnd.shuffle(rows)
    per = defaultdict(int)
    picked = []
    for r in rows:
        key = (r["split"], r["style"])
        if r["split"] == "train" and per[key] >= CAP.get(r["style"], 10 ** 9):
            continue
        per[key] += 1
        picked.append(r)
    print(f"Dialogs: женских фраз {len(rows)}, взято {len(picked)}", flush=True)

    # эталоны: по 24 нейтральных фразы на актрису (не из взятых в учёбу)
    used = {r["audio_path"] for r in picked}
    refs = defaultdict(list)
    for r in rows:
        if r["style"] == "calm" and r["audio_path"] not in used and len(refs[r["speaker_id"]]) < 24:
            w = to24k(SRC / r["audio_path"])
            if w is not None and 3.0 <= len(w) / SR <= 9.0:
                p = WAVS / f"ref_{Path(r['audio_path']).stem}.wav"
                sf.write(p, w, SR)
                refs[r["speaker_id"]].append(str(p))
    print("эталонов:", {k: len(v) for k, v in refs.items()}, flush=True)

    items = []
    for i, r in enumerate(picked):
        w = to24k(SRC / r["audio_path"])
        if w is None:
            continue
        p = WAVS / f"{Path(r['audio_path']).stem}.wav"
        sf.write(p, w, SR)
        items.append({"audio": str(p), "text": marked(r["accent_text"] or r["text"]), "style": r["style"],
                      "instruct": ins[r["style"]], "ref": rnd.choice(refs[r["speaker_id"]]), "split": r["split"]})
        if i % 500 == 0:
            print(f"звук: {i}/{len(picked)}", flush=True)

    from qwen_tts import Qwen3TTSTokenizer
    tok = Qwen3TTSTokenizer.from_pretrained(str(TOKENIZER), device_map="cuda:0")
    for i in range(0, len(items), 16):
        batch = items[i:i + 16]
        with torch.inference_mode():
            enc = tok.encode([it["audio"] for it in batch])
        for it, codes in zip(batch, enc.audio_codes):
            it["codes"] = codes.cpu().tolist()
        if i % 320 == 0:
            print(f"коды: {i + len(batch)}/{len(items)}", flush=True)
    counts = defaultdict(int)
    for split in ("train", "val"):
        with open(OUT / f"{split}_dialogs.jsonl", "w", encoding="utf-8", newline="\n") as f:
            for it in items:
                if it["split"] != split:
                    continue
                counts[(split, it["style"])] += 1
                f.write(json.dumps({k: it[k] for k in ("audio", "text", "style", "instruct", "ref", "codes")}, ensure_ascii=False) + "\n")
    print("по стилям:", dict(sorted(counts.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
