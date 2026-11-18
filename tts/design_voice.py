"""Сборка голоса Клавы по текстовому описанию (Qwen3-TTS VoiceDesign 1.7B, Apache-2.0).

Генерирует кандидатов одной и той же русской фразой по разным описаниям голоса,
меряет высоту тона, скорость синтеза и пик видеопамяти. Кандидаты кладёт в
tts/voices/candidates и копию в web/voices, чтобы послушать в браузере и выбрать.

Запуск (видеокарта должна быть свободна — сервер FlashNext погашен):
    H:\\Neiro\\Neko\\tts\\.venv\\Scripts\\python.exe design_voice.py
"""
import json
import shutil
import time
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from qwen_tts import Qwen3TTSModel

ROOT = Path(__file__).resolve().parent.parent
MODEL = ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-VoiceDesign"
OUT = ROOT / "tts" / "voices" / "candidates"
WEB = ROOT / "web" / "voices"

TEXT = ("Приветик! Я Клава, твоя кошечка. Мур-мур! "
        "Как же я рада тебя видеть. Давай сегодня сделаем что-нибудь классное, а?")

# Клава — взрослая девушка лет двадцати трёх: голос милый и кавайный, но не детский.
# Первый набор (mia_*) вышел с тоном ~500 Гц — это уже «лолька»; цель — примерно 260..340 Гц.
CANDIDATES = [
    ("Sweet, gentle young woman in her early twenties, soft and warm mid-high voice, cute and "
     "playful like a friendly anime catgirl, but clearly an adult, not a child. Clear, natural speech.", 101),
    ("Warm, soothing female voice of a twenty-three-year-old woman, a gentle smile in the voice, "
     "slightly breathy, tender and affectionate, cute but grown-up, medium pitch.", 202),
    ("Cheerful kawaii young woman, bright and melodic voice of medium pitch, playful teasing "
     "intonation, confident and friendly, adult anime heroine, not high-pitched.", 303),
    ("Soft, velvety and cute young adult female voice, calm and cozy, a little purring and "
     "relaxed, like a cat girl lounging on a sofa, lower-mid pitch, very pleasant.", 404),
    ("温柔甜美的年轻女性声音，二十多岁，音调中等，可爱但成熟，不像小孩，语气亲切活泼，像动漫里的猫娘姐姐。", 505),
    ("Charming young woman, sweet voice with a light husky warmth, gentle and a bit flirty, "
     "kawaii but mature, relaxed natural pace, medium pitch.", 606),
]


def median_f0(wav: np.ndarray, sr: int) -> float:
    """Медиана основного тона по озвученным кадрам 40 мс (автокорреляция, 80..600 Гц)."""
    frame = int(sr * 0.04)
    lo, hi = int(sr / 600), int(sr / 80)
    f0s = []
    for i in range(0, len(wav) - frame, frame):
        x = wav[i:i + frame].astype(np.float64)
        x -= x.mean()
        if np.sqrt(np.mean(x * x)) < 0.02:
            continue
        ac = np.correlate(x, x, mode="full")[frame - 1:]
        if ac[0] <= 0:
            continue
        lag = lo + int(np.argmax(ac[lo:hi]))
        if ac[lag] / ac[0] > 0.3:
            f0s.append(sr / lag)
    return float(np.median(f0s)) if f0s else 0.0


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    WEB.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    model = Qwen3TTSModel.from_pretrained(
        str(MODEL), device_map="cuda:0", dtype=torch.bfloat16, attn_implementation="sdpa")
    print(f"модель загружена за {time.time() - t0:.1f} с, "
          f"VRAM {torch.cuda.memory_allocated() / 2**30:.2f} ГиБ", flush=True)

    report = []
    for n, (instruct, seed) in enumerate(CANDIDATES, 1):
        torch.manual_seed(seed)
        torch.cuda.reset_peak_memory_stats()
        t = time.time()
        wavs, sr = model.generate_voice_design(text=TEXT, language="Russian", instruct=instruct)
        took = time.time() - t
        wav = np.asarray(wavs[0], dtype=np.float32)
        dur = len(wav) / sr
        name = f"klava_{n:02d}.wav"
        sf.write(OUT / name, wav, sr)
        shutil.copyfile(OUT / name, WEB / name)
        row = {"file": name, "instruct": instruct, "seed": seed, "sr": sr,
               "duration_s": round(dur, 2), "gen_s": round(took, 2), "rtf": round(took / dur, 2),
               "f0_hz": round(median_f0(wav, sr), 1),
               "peak_vram_gib": round(torch.cuda.max_memory_allocated() / 2**30, 2)}
        report.append(row)
        print(json.dumps(row, ensure_ascii=False), flush=True)

    (OUT / "candidates.json").write_text(
        json.dumps({"text": TEXT, "candidates": report}, ensure_ascii=False, indent=1), encoding="utf-8")
    shutil.copyfile(OUT / "candidates.json", WEB / "candidates.json")
    print("ГОТОВО", flush=True)


if __name__ == "__main__":
    main()
