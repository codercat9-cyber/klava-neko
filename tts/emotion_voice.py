"""Эмоциональные эталоны голоса Клавы (одноразовая сборка, нейросеть Qwen3-TTS).

Живой голос клонирует тембр И манеру одного эталона (klava_02 — тёплый, с улыбкой), поэтому злые и грустные фразы
звучат так же ласково. Здесь на каждую эмоцию собираются кандидаты тремя путями:
  design — VoiceDesign по исходному описанию klava_02 (тот же seed 202 и ещё три) + манера эмоции;
  instr  — Base с эталоном klava_02 и указанием манеры (instruct);
  xvec   — Base только по тембру эталона (xvec_only, без его манеры) + указание манеры.
Каждый кандидат меряется: близость тембра к klava_02 (WavLM x-vector, как в профилях голосов: свой ≈ 0,96,
чужие 0,30–0,65), громкость, высота тона и её разброс, темп. Лучший design-кандидат потом проверяется в деле:
живым Base-клоном по нему как по эталону — держится ли тембр и переходит ли манера.

Итог: tts/voices/emotions/*.wav + emotions.json, копии и страница прослушивания web/voices/emotions.html.

    tts\\.venv\\Scripts\\python.exe emotion_voice.py [--only angry,laugh] [--seeds 202,11,22,33]
"""
from __future__ import annotations

import argparse
import gc
import json
import shutil
import sys
import time
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from faster_qwen3_tts import FasterQwen3TTS

import ru_text
import speaker_id
from design_voice import median_f0

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "models"
DESIGN = MODELS / "Qwen3-TTS-12Hz-1.7B-VoiceDesign"
BASE = MODELS / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress"
REF = ROOT / "tts" / "voices" / "candidates" / "klava_02.wav"
REF_TEXT = "Приветик! Я Клава, твоя кошечка. Мур-мур! Как же я рада тебя видеть. Давай сегодня сделаем что-нибудь классное, а?"
OUT = ROOT / "tts" / "voices" / "emotions"
WEB = ROOT / "web" / "voices" / "emotions"

# тембр Клавы — описание, из которого вышел klava_02 (без «улыбки»: манеру задаёт эмоция)
TIMBRE = ("Female voice of a twenty-three-year-old woman, cute but grown-up, medium pitch, slightly breathy, "
          "a playful anime catgirl.")

# эмоция: (манера для модели, фраза-эталон — живая, в характере, 6–10 с)
EMOTIONS: dict[str, tuple[str, str]] = {
    "happy": ("Very happy and excited, bright energetic voice, big smile, lively fast intonation.",
              "Ура, ну наконец-то! Это же просто охренительно, я прям прыгаю от радости, мур! Давай ещё!"),
    "laugh": ("Laughing out loud while talking, giggling between words, amused and cheerful.",
              "Ха-ха-ха, ну ты даёшь! Ой, не могу, живот болит, ха-ха, вот это ты отжёг!"),
    "angry": ("Furious and angry, shouting loudly, tense aggressive voice, yelling at someone.",
              "Да ты издеваешься?! Я тебе СКОЛЬКО раз говорила, не трогай мои файлы! Всё, я злая!"),
    "annoyed": ("Irritated and grumpy, grumbling, annoyed sighs, clipped sharp words.",
                "Пфф, ну опять двадцать пять. Сколько можно, а? Ладно, сделаю, но я недовольна."),
    "sad": ("Sad and upset, quiet slow voice, sighing, on the verge of tears, low energy.",
            "Эх... Мне сегодня как-то грустно. Ты весь день молчишь, а я тут одна сижу..."),
    "surprised": ("Very surprised and shocked, gasping, high-pitched amazed exclamations.",
                  "Ого! Ничего себе! Серьёзно?! Да ладно, не может быть, вот это поворот!"),
    "shy": ("Shy and embarrassed, soft hesitant voice, flustered, speaking a little quietly with pauses.",
            "Ой... ну ты чего так смотришь... Мне аж неловко стало, я вся покраснела, мур."),
    "tender": ("Tender and affectionate, soft intimate half-whisper, purring warmly, very close to the listener.",
               "Мурр... иди сюда, мой хороший. Я так соскучилась. Давай просто посидим рядышком, ладно?"),
    "sly": ("Teasing and sarcastic, smirking, playful mischievous tone, drawn-out sly words.",
            "Ну-ну, конечно, ты у нас самый умный. Ага, верю-верю. Хи-хи, а покраснел-то чего?"),
    "whisper": ("Whispering quietly, secretive hushed voice, like telling a secret right into the ear.",
                "Тс-с... слушай, только никому не говори. У меня есть секрет, и он тебе понравится."),
}

# проверочная фраза для клона по кандидату (эмоцию несёт эталон, не текст)
PROBE = "Слушай, я вот что тебе скажу: сегодня всё будет совсем по-другому."


def log(m: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {m}", flush=True)


def features(wav: np.ndarray, sr: int, text: str) -> dict:
    """Громкость (RMS речи, дБ), тон (медиана и разброс, Гц), темп (символов речи в секунду)."""
    x = wav.astype(np.float64)
    frame = int(sr * 0.04)
    rms = np.array([np.sqrt(np.mean(x[i:i + frame] ** 2)) for i in range(0, len(x) - frame, frame)])
    voiced = rms[rms > 0.01]
    lo, hi = int(sr / 600), int(sr / 80)
    f0s = []
    for i in range(0, len(x) - frame, frame):
        seg = x[i:i + frame] - x[i:i + frame].mean()
        if np.sqrt(np.mean(seg * seg)) < 0.02:
            continue
        ac = np.correlate(seg, seg, mode="full")[frame - 1:]
        if ac[0] <= 0:
            continue
        lag = lo + int(np.argmax(ac[lo:hi]))
        if ac[lag] / ac[0] > 0.3:
            f0s.append(sr / lag)
    speech_s = max(0.1, len(voiced) * 0.04)
    letters = sum(ch.isalpha() for ch in text)
    return {
        "dur_s": round(len(wav) / sr, 2),
        "loud_db": round(20 * np.log10(float(np.mean(voiced)) + 1e-9), 1) if len(voiced) else -99.0,
        "f0_hz": round(float(np.median(f0s)), 1) if f0s else 0.0,
        "f0_std": round(float(np.std(f0s)), 1) if f0s else 0.0,
        "rate": round(letters / speech_s, 1),
    }


def to16k(wav: np.ndarray, sr: int) -> np.ndarray:
    import librosa
    return librosa.resample(wav.astype(np.float32), orig_sr=sr, target_sr=16000)


def similarity(ref_emb: np.ndarray, wav: np.ndarray, sr: int) -> float:
    e = speaker_id.embed_audio(to16k(wav, sr))
    return round(float(np.dot(ref_emb, e) / (np.linalg.norm(ref_emb) * np.linalg.norm(e) + 1e-9)), 3)


def load(model_dir: Path) -> FasterQwen3TTS:
    t = time.time()
    m = FasterQwen3TTS.from_pretrained(str(model_dir), device="cuda", dtype=torch.bfloat16,
                                       attn_implementation="sdpa", max_seq_len=1024)
    log(f"модель {model_dir.name} за {time.time() - t:.1f} с, VRAM {torch.cuda.memory_allocated() / 2**30:.2f} ГиБ")
    return m


def free() -> None:
    """После того как вызывающий отпустил модель (присвоил None) — вернуть видеопамять."""
    gc.collect()
    torch.cuda.empty_cache()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--seeds", default="202,11,22,33")
    a = ap.parse_args()
    emos = {k: v for k, v in EMOTIONS.items() if not a.only or k in a.only.split(",")}
    seeds = [int(s) for s in a.seeds.split(",")]
    OUT.mkdir(parents=True, exist_ok=True)
    WEB.mkdir(parents=True, exist_ok=True)

    speaker_id.load_encoder()
    ref_wav, ref_sr = sf.read(REF, dtype="float32")
    ref_emb = speaker_id.embed_audio(to16k(ref_wav, ref_sr))
    base_f = features(ref_wav, ref_sr, REF_TEXT)
    log(f"эталон klava_02: {base_f}")
    ru_text.load()
    rows: list[dict] = []

    def keep(emo: str, way: str, seed: int, wav: np.ndarray, sr: int, text: str, took: float, instruct: str) -> dict:
        name = f"{emo}_{way}_{seed}.wav"
        sf.write(OUT / name, wav, sr)
        shutil.copyfile(OUT / name, WEB / name)
        row = {"emotion": emo, "way": way, "seed": seed, "file": name, "text": text, "instruct": instruct,
               "gen_s": round(took, 1), "sim": similarity(ref_emb, wav, sr), **features(wav, sr, text)}
        rows.append(row)
        log(json.dumps(row, ensure_ascii=False))
        return row

    # --- A: VoiceDesign — свой эталон на каждую эмоцию
    design = load(DESIGN)
    for emo, (manner, text) in emos.items():
        to_model = ru_text.prepare(text)[0].replace(ru_text.ACUTE, "")   # у VoiceDesign нет адаптера ударений
        for seed in seeds:
            torch.manual_seed(seed)
            t = time.time()
            wavs, sr = design.generate_voice_design(text=to_model, instruct=f"{TIMBRE} {manner}", language="Russian",
                                                    temperature=0.9, top_k=50, top_p=1.0)
            keep(emo, "design", seed, np.asarray(wavs[0], dtype=np.float32), sr, text, time.time() - t, manner)
    design = None
    free()

    # --- B, C: живой Base — указание манеры с эталоном и только по тембру; проверка лучших design-эталонов
    base = load(BASE)
    ref_text = ru_text.prepare(REF_TEXT)[0]
    for emo, (manner, text) in emos.items():
        to_model = ru_text.prepare(text)[0]
        for way, kw in (("instr", {}), ("xvec", {"xvec_only": True})):
            torch.manual_seed(202)
            t = time.time()
            wavs, sr = base.generate_voice_clone(text=to_model, language="Russian", ref_audio=str(REF), ref_text=ref_text,
                                                 instruct=manner, temperature=0.55, top_k=20, top_p=0.9, **kw)
            keep(emo, way, 202, np.asarray(wavs[0], dtype=np.float32), sr, text, time.time() - t, manner)
        # лучший по тембру design-кандидат этой эмоции — как эталон для живого голоса
        cands = [r for r in rows if r["emotion"] == emo and r["way"] == "design"]
        if not cands:
            continue
        best = max(cands, key=lambda r: r["sim"])
        t = time.time()
        wavs, sr = base.generate_voice_clone(text=ru_text.prepare(PROBE)[0], language="Russian", ref_audio=str(OUT / best["file"]),
                                             ref_text=ru_text.prepare(best["text"])[0], temperature=0.55, top_k=20, top_p=0.9)
        keep(emo, f"live{best['seed']}", best["seed"], np.asarray(wavs[0], dtype=np.float32), sr, PROBE, time.time() - t,
             f"живой клон по эталону {best['file']}")
    base = None
    free()

    report = {"reference": {"file": "klava_02.wav", **base_f}, "timbre": TIMBRE, "rows": rows}
    (OUT / "emotions.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    shutil.copyfile(OUT / "emotions.json", WEB / "emotions.json")
    write_page(report)
    log("ГОТОВО")
    return 0


def write_page(report: dict) -> None:
    """Страница прослушивания: по эмоциям, у каждого кандидата — плеер и замеры."""
    names = {"happy": "радость", "laugh": "смех", "angry": "злость", "annoyed": "раздражение", "sad": "грусть",
             "surprised": "удивление", "shy": "смущение", "tender": "нежность", "sly": "хитрость", "whisper": "шёпот"}
    ways = {"design": "свой эталон (VoiceDesign)", "instr": "живой голос + манера", "xvec": "только тембр + манера"}
    rows = report["rows"]
    parts = ["<!doctype html><meta charset='utf-8'><title>Голос Клавы: эмоции</title>",
             "<style>body{font:14px Segoe UI,sans-serif;background:#16141f;color:#eee;margin:16px}"
             "h2{color:#7ff3ff;margin-top:28px}table{border-collapse:collapse}td{padding:4px 8px;border-bottom:1px solid #333}"
             "audio{height:30px}.hi{color:#b8ff6a}.lo{color:#ff7f9f}</style>",
             "<h1>Голос Клавы: эмоции</h1><p>Эталон: <audio controls src='../candidates/klava_02.wav'></audio> "
             f"(тон {report['reference']['f0_hz']} Гц, громкость {report['reference']['loud_db']} дБ). "
             "«Тембр» — близость к Клаве (свой голос ≈ 0,9+, чужой &lt; 0,65).</p>"]
    for emo, ru in names.items():
        rs = [r for r in rows if r["emotion"] == emo]
        if not rs:
            continue
        parts.append(f"<h2>{ru} ({emo})</h2><table><tr><td></td><td>путь</td><td>тембр</td><td>тон</td><td>разброс</td>"
                     "<td>громк.</td><td>темп</td><td>фраза</td></tr>")
        for r in rs:
            way = ways.get(r["way"], "живой клон по лучшему своему эталону")
            cls = "hi" if r["sim"] >= 0.85 else "lo" if r["sim"] < 0.65 else ""
            parts.append(f"<tr><td><audio controls src='{r['file']}'></audio></td><td>{way} · seed {r['seed']}</td>"
                         f"<td class='{cls}'>{r['sim']:.2f}</td><td>{r['f0_hz']}</td><td>{r['f0_std']}</td><td>{r['loud_db']}</td>"
                         f"<td>{r['rate']}</td><td>{r['text']}</td></tr>")
        parts.append("</table>")
    (WEB / "emotions.html").write_text("\n".join(parts), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
