"""Данные для эмоциональной LoRA голоса Клавы (нейросеть, одноразовая подготовка).

RESD (Aniemore, русская эмоциональная речь, 7 эмоций, у каждой фразы текст) → 24 кГц без тишины по краям → коды
кодека Qwen3-TTS (12 Гц, 16 групп). Эмоция → «манера» — та же строка, что сервер голоса подаёт как instruct
(tts/emotions.json), нейтральная речь — без указания: так спокойный голос остаётся прежним.

Эталон голоса (x-vector) — ДРУГАЯ фраза того же актёра: иначе эмоция протекает через отпечаток, и модель учится
брать её оттуда, а не из указания. Актёров в диалоге двое — фразы каждой папки делятся на двоих по отпечатку WavLM.

Шёпота в RESD нет — он делается из обычной речи (LPC: голосовые связки заменяются шумом, тракт и слова те же).

    tts\\.venv\\Scripts\\python.exe emo_train\\prepare.py
Итог: data/emotion/emo24/*.wav, data/emotion/train.jsonl и val.jsonl ({audio, text, style, instruct, ref, codes}).
"""
from __future__ import annotations

import io
import json
import random
import sys
from collections import defaultdict
from pathlib import Path

import librosa
import numpy as np
import pyarrow.parquet as pq
import scipy.signal as sps
import soundfile as sf
import torch

TTS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TTS))
import speaker_id  # noqa: E402

ROOT = TTS.parent
SRC = ROOT / "data" / "emotion" / "resd_annotated" / "data"
OUT = ROOT / "data" / "emotion"
WAVS = OUT / "emo24"
TOKENIZER = ROOT / "models" / "Qwen3-TTS-Tokenizer-12Hz"
SR = 24000

# RESD → голос Клавы: стиль и указание манеры (строки — как в tts/emotions.json)
STYLES = {
    "anger": ("angry", "Furious and angry, shouting loudly, tense aggressive voice, yelling at someone."),
    "disgust": ("annoyed", "Irritated and grumpy, grumbling, annoyed sighs, clipped sharp words, low pitch."),
    "enthusiasm": ("happy", "Happy and excited, warm energetic voice with a smile, lively intonation, natural medium pitch, not high-pitched."),
    "happiness": ("happy", "Happy and excited, warm energetic voice with a smile, lively intonation, natural medium pitch, not high-pitched."),
    "sadness": ("sad", "Sad and upset, quiet slow voice, sighing, on the verge of tears."),
    "fear": ("scared", "Scared and anxious, trembling tense voice, nervous quick breaths."),
    "neutral": ("calm", ""),
}
WHISPER = ("whisper", "Soft breathy half-whisper, secretive and intimate, but every word clearly audible.")


def load_rows() -> list[dict]:
    rows = []
    for f in sorted(SRC.glob("*.parquet")):
        split = "val" if f.name.startswith("test") else "train"
        for r in pq.read_table(f).to_pylist():
            r["split"] = split
            rows.append(r)
    return rows


def to24k(raw: bytes) -> np.ndarray | None:
    wav, sr = sf.read(io.BytesIO(raw), dtype="float32", always_2d=False)
    if wav.ndim > 1:
        wav = wav.mean(axis=1)
    if sr != SR:
        wav = librosa.resample(wav, orig_sr=sr, target_sr=SR)
    wav, _ = librosa.effects.trim(wav, top_db=35)
    if not (0.6 <= len(wav) / SR <= 12.0):
        return None
    peak = float(np.max(np.abs(wav)))
    return (wav / peak * 0.9).astype(np.float32) if peak > 0 else None


def whisperize(wav: np.ndarray, order: int = 22) -> np.ndarray:
    """LPC-шёпот: огибающая тракта кадра та же, возбуждение — белый шум той же энергии; низ под 300 Гц срезан."""
    frame, hop = int(0.025 * SR), int(0.0125 * SR)
    win = np.hanning(frame).astype(np.float32)
    out = np.zeros(len(wav) + frame, dtype=np.float32)
    norm = np.zeros_like(out)
    rng = np.random.default_rng(len(wav))
    for i in range(0, len(wav) - frame, hop):
        x = wav[i:i + frame] * win
        if np.sqrt(np.mean(x * x)) < 1e-4:
            continue
        a = librosa.lpc(x.astype(np.float64) + 1e-9 * rng.standard_normal(frame), order=order)
        resid = sps.lfilter(a, [1.0], x)
        noise = rng.standard_normal(frame) * np.sqrt(np.mean(resid ** 2))
        y = sps.lfilter([1.0], a, noise).astype(np.float32) * win
        out[i:i + frame] += y
        norm[i:i + frame] += win * win
    out = out[:len(wav)] / np.maximum(norm[:len(wav)], 1e-3)
    b, a = sps.butter(4, 300 / (SR / 2), "high")
    out = sps.lfilter(b, a, out).astype(np.float32)
    peak = float(np.max(np.abs(out)))
    return out / peak * 0.8 if peak > 0 else out


def speakers(items: list[dict]) -> None:
    """Двое актёров на диалог: фразы папки делятся надвое по отпечатку (2 центра, косинус) → item['spk']."""
    by_dialog = defaultdict(list)
    for it in items:
        by_dialog[it["dialog"]].append(it)
    for d, group in by_dialog.items():
        embs = np.stack([it["emb"] for it in group])
        embs = embs / np.linalg.norm(embs, axis=1, keepdims=True)
        # два самых далёких — начальные центры, три прохода k-средних
        sim = embs @ embs.T
        i, j = np.unravel_index(np.argmin(sim), sim.shape)
        cents = embs[[i, j]]
        for _ in range(3):
            lab = np.argmax(embs @ cents.T, axis=1)
            for k in range(2):
                if (lab == k).any():
                    c = embs[lab == k].mean(axis=0)
                    cents[k] = c / np.linalg.norm(c)
        for it, k in zip(group, lab):
            it["spk"] = f"{d}_{k}"


def main() -> int:
    WAVS.mkdir(parents=True, exist_ok=True)
    speaker_id.load_encoder()
    rows = load_rows()
    print(f"RESD: {len(rows)} фраз", flush=True)
    items = []
    for r in rows:
        wav = to24k(r["speech"]["bytes"])
        if wav is None or not r.get("text"):
            continue
        name = r["name"].replace(" ", "_")
        path = WAVS / f"{name}.wav"
        sf.write(path, wav, SR)
        style, instruct = STYLES[r["emotion"]]
        items.append({"audio": str(path), "text": r["text"].strip(), "style": style, "instruct": instruct,
                      "split": r["split"], "dialog": r["name"].split("_")[0],
                      "emb": speaker_id.embed_audio(librosa.resample(wav, orig_sr=SR, target_sr=16000))})
    print(f"годных: {len(items)}", flush=True)
    speakers(items)
    by_spk = defaultdict(list)
    for it in items:
        by_spk[it["spk"]].append(it)
    rnd = random.Random(7)
    for it in items:
        # эталон — другая фраза того же актёра, лучше нейтральная (без эмоции в отпечатке)
        same = [x for x in by_spk[it["spk"]] if x is not it]
        calm = [x for x in same if x["style"] == "calm"]
        pick = rnd.choice(calm or same) if same else it
        it["ref"] = pick["audio"]
    # шёпот: из нейтральных и части прочих — 260 фраз
    pool = [it for it in items if it["split"] == "train"]
    rnd.shuffle(pool)
    extra = []
    for it in pool[:260]:
        wav, _ = sf.read(it["audio"], dtype="float32")
        path = WAVS / (Path(it["audio"]).stem + "_whisper.wav")
        sf.write(path, whisperize(wav), SR)
        extra.append({**it, "audio": str(path), "style": WHISPER[0], "instruct": WHISPER[1]})
    items += extra
    print(f"с шёпотом: {len(items)}", flush=True)

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
        with open(OUT / f"{split}.jsonl", "w", encoding="utf-8") as f:
            for it in items:
                if it["split"] != split:
                    continue
                counts[(split, it["style"])] += 1
                f.write(json.dumps({k: it[k] for k in ("audio", "text", "style", "instruct", "ref", "codes")}, ensure_ascii=False) + "\n")
    print("по стилям:", dict(counts))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
