"""Смех ВНУТРИ речи для LoRA голоса Клавы (нейросеть, одноразовая подготовка).

Друг 28.09: «смех не сработал — как будто текстом „Аха-ха-ха“, не живо»; смешки перед фразой (VoiceDesign, Seed-VC) —
то «ахегао», то «обезьянье урчание». Живой человек смеётся прямо посреди слов. NonverbalTTS (deepvk, Apache-2.0,
data/emotion/nvtts): английская речь VoxCeleb/Expresso с разметкой невербального прямо в тексте, 🤣 — смех на своём месте.
Берутся женские голоса хорошего качества (DNSMOS > 3) со смехом внутри фразы; в тексте остаётся только 🤣 (прочие значки —
дыхание, кашель — убираются). Модель учится: 🤣 в тексте — засмеяться тут. Сервер голоса заменяет «ха-ха» на 🤣.

Эталон голоса (x-vector) — другая запись того же человека, лучше без смеха: смех должен идти из текста и манеры, а не из
отпечатка. Язык в разметке — english (как в данных).

    tts\\.venv\\Scripts\\python.exe emo_train\\prepare_nvtts.py
Итог: data/emotion/nv24/*.wav, data/emotion/train_nv.jsonl и val_nv.jsonl ({audio, text, style, instruct, ref, codes, lang}).
"""
from __future__ import annotations

import io
import json
import random
import re
import sys
from collections import defaultdict
from pathlib import Path

import librosa
import numpy as np
import pyarrow.parquet as pq
import soundfile as sf
import torch

TTS = Path(__file__).resolve().parent.parent
ROOT = TTS.parent
SRC = ROOT / "data" / "emotion" / "nvtts"
OUT = ROOT / "data" / "emotion"
WAVS = OUT / "nv24"
TOKENIZER = ROOT / "models" / "Qwen3-TTS-Tokenizer-12Hz"
SR = 24000
LAUGH = "🤣"
# манера для смеха в речи — та же строка идёт в tts/emotions.json у стиля laugh
STYLE = ("laughtalk", "Amused and cheerful, laughing naturally while talking where it is funny, clear words, natural medium-low pitch.")
OTHER_MARKS = re.compile("[\U0001F32C\U0001F443\U0001F637\U0001F5E3\U0001F624\U0001F927️‍]")


def clean(text: str) -> str:
    t = OTHER_MARKS.sub(" ", text)
    t = re.sub(r"\s+", " ", t).strip()
    return t


def to24k(raw: bytes) -> np.ndarray | None:
    wav, sr = sf.read(io.BytesIO(raw), dtype="float32", always_2d=False)
    if wav.ndim > 1:
        wav = wav.mean(axis=1)
    if sr != SR:
        wav = librosa.resample(wav, orig_sr=sr, target_sr=SR)
    wav, _ = librosa.effects.trim(wav, top_db=35)
    if not (1.2 <= len(wav) / SR <= 13.0):
        return None
    peak = float(np.max(np.abs(wav)))
    return (wav / peak * 0.9).astype(np.float32) if peak > 0 else None


def main() -> int:
    WAVS.mkdir(parents=True, exist_ok=True)
    files = sorted(SRC.rglob("*.parquet"))
    cols = ["index", "audio", "Result", "gender", "dnsmos", "duration", "speaker_id"]
    laughs, plain = [], defaultdict(list)
    for f in files:
        for r in pq.read_table(f, columns=cols).to_pylist():
            if r["gender"] != "f" or (r["dnsmos"] or 0) < 3.0 or not r["Result"]:
                continue
            (laughs.append(r) if LAUGH in r["Result"] else plain[r["speaker_id"]].append(r))
    print(f"женского смеха в речи: {len(laughs)}; спикеров с записями без смеха: {len(plain)}", flush=True)
    rnd = random.Random(7)
    items = []
    by_spk = defaultdict(list)
    for r in laughs:
        wav = to24k(r["audio"]["bytes"])
        text = clean(r["Result"])
        if wav is None or LAUGH not in text or len(re.sub(r"[^A-Za-z]", "", text)) < 6:
            continue
        name = re.sub(r"[^\w]", "_", r["index"])
        path = WAVS / f"{name}.wav"
        sf.write(path, wav, SR)
        it = {"audio": str(path), "text": text, "style": STYLE[0], "instruct": STYLE[1], "lang": "english", "spk": r["speaker_id"]}
        items.append(it)
        by_spk[r["speaker_id"]].append(it)
    print(f"годных: {len(items)}", flush=True)
    # эталон — запись того же человека без смеха (если есть), иначе другая со смехом
    refs: dict[str, str] = {}
    for spk, rows in plain.items():
        if spk not in by_spk:
            continue
        for r in rows[:3]:
            wav = to24k(r["audio"]["bytes"])
            if wav is not None:
                path = WAVS / f"ref_{re.sub(r'[^\w]', '_', r['index'])}.wav"
                sf.write(path, wav, SR)
                refs[spk] = str(path)
                break
    for it in items:
        same = [x for x in by_spk[it["spk"]] if x is not it]
        it["ref"] = refs.get(it["spk"]) or (rnd.choice(same)["audio"] if same else it["audio"])
    # проверка — по людям, которых нет в учёбе (иначе смех выучится «по голосу»)
    spks = sorted(by_spk)
    rnd.shuffle(spks)
    val_spk = set(spks[: max(3, len(spks) // 10)])
    for it in items:
        it["split"] = "val" if it["spk"] in val_spk else "train"

    from qwen_tts import Qwen3TTSTokenizer
    tok = Qwen3TTSTokenizer.from_pretrained(str(TOKENIZER), device_map="cuda:0")
    for i in range(0, len(items), 16):
        batch = items[i:i + 16]
        with torch.inference_mode():
            enc = tok.encode([it["audio"] for it in batch])
        for it, codes in zip(batch, enc.audio_codes):
            it["codes"] = codes.cpu().tolist()
        if i % 160 == 0:
            print(f"коды: {i + len(batch)}/{len(items)}", flush=True)
    counts = defaultdict(int)
    for split in ("train", "val"):
        with open(OUT / f"{split}_nv.jsonl", "w", encoding="utf-8") as f:
            for it in items:
                if it["split"] != split:
                    continue
                counts[split] += 1
                f.write(json.dumps({k: it[k] for k in ("audio", "text", "style", "instruct", "ref", "codes", "lang")}, ensure_ascii=False) + "\n")
    print("фраз:", dict(counts), "пример:", items[0]["text"][:120])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
