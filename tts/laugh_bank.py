"""Банк живых смешков Клавы (одноразовая сборка, нейросеть Qwen3-TTS VoiceDesign).

Друг 28.09: «смех не сработал — как будто текстом „Аха-ха-ха“ читает, не живо». Манера «смеётся» у клона даёт
чтение слогов. Здесь — отдельные смешки без слов: VoiceDesign по тембру Клавы с манерой «заливается смехом»
на коротких «текстах» из одних смешков, несколько seed. Каждый меряется: близость тембра к klava_02 (WavLM) и
«на смех ли похоже»: доля придыхательных (незвонких) кадров и рваность громкости — у чтения слогов она ровная.
Лучших друг отмечает на странице web/voices/emotions/laughs.html, отмеченные сервер голоса вклеивает вместо «ха-ха».

    tts\\.venv\\Scripts\\python.exe laugh_bank.py [--seeds 1,2,3,4,5,6]
"""
import argparse
import json
import shutil
import time
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from faster_qwen3_tts import FasterQwen3TTS

import speaker_id
from emotion_voice import REF, TIMBRE, to16k

ROOT = Path(__file__).resolve().parent.parent
DESIGN = ROOT / "models" / "Qwen3-TTS-12Hz-1.7B-VoiceDesign"
OUT = ROOT / "tts" / "voices" / "laughs"
WEB = ROOT / "web" / "voices" / "emotions" / "laughs"

KINDS = {
    "giggle": ("Giggling softly and playfully, short cute breathy giggles, no words, genuine amusement.", "Хи-хи-хи... хи-хи."),
    "chuckle": ("A short genuine chuckle through the nose, amused, no words, relaxed.", "Хм-хм-хм."),
    "laugh": ("Bursting into genuine hearty laughter, breathless, no words, really finds it funny.", "Ахаха-ха-ха-ха!"),
    "cackle": ("Uncontrollable laughter, gasping for air between laughs, no words, tears of laughter.", "Ахахахаха! Ха-ха-ха... ах-ха-ха!"),
}


def laughiness(wav: np.ndarray, sr: int) -> tuple[float, float]:
    """(доля придыхательных кадров среди звучащих, рваность громкости — CV огибающей)."""
    frame = int(sr * 0.03)
    n = len(wav) // frame
    x = wav[:n * frame].reshape(n, frame).astype(np.float64)
    rms = np.sqrt(np.mean(x ** 2, axis=1))
    loud = rms > 0.01
    if loud.sum() < 5:
        return 0.0, 0.0
    zcr = np.mean(np.abs(np.diff(np.sign(x), axis=1)) > 0, axis=1)
    breathy = float(np.mean(zcr[loud] > 0.12))
    env = rms[loud]
    return round(breathy, 2), round(float(np.std(env) / (np.mean(env) + 1e-9)), 2)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", default="1,2,3,4,5,6")
    a = ap.parse_args()
    seeds = [int(s) for s in a.seeds.split(",")]
    OUT.mkdir(parents=True, exist_ok=True)
    WEB.mkdir(parents=True, exist_ok=True)
    speaker_id.load_encoder()
    rw, rs = sf.read(REF, dtype="float32")
    remb = speaker_id.embed_audio(to16k(rw, rs))
    model = FasterQwen3TTS.from_pretrained(str(DESIGN), device="cuda", dtype=torch.bfloat16, attn_implementation="sdpa", max_seq_len=1024)
    rows = []
    for kind, (manner, text) in KINDS.items():
        for seed in seeds:
            torch.manual_seed(seed)
            t = time.time()
            wavs, sr = model.generate_voice_design(text=text, instruct=f"{TIMBRE} {manner}", language="Russian",
                                                   temperature=0.95, top_k=60, top_p=1.0)
            wav = np.asarray(wavs[0], dtype=np.float32)
            # хвост тишины обрезать: в банке нужен сам смешок
            env = np.abs(wav) > 0.01
            if env.any():
                last = len(wav) - int(np.argmax(env[::-1]))
                wav = wav[:min(len(wav), last + int(0.08 * sr))]
            name = f"{kind}_{seed}.wav"
            sf.write(OUT / name, wav, sr)
            shutil.copyfile(OUT / name, WEB / name)
            e = speaker_id.embed_audio(to16k(wav, sr))
            sim = float(np.dot(remb, e) / (np.linalg.norm(remb) * np.linalg.norm(e) + 1e-9))
            br, cv = laughiness(wav, sr)
            row = {"kind": kind, "seed": seed, "file": name, "dur_s": round(len(wav) / sr, 2), "sim": round(sim, 3),
                   "breathy": br, "choppy": cv, "gen_s": round(time.time() - t, 1)}
            rows.append(row)
            print(json.dumps(row, ensure_ascii=False), flush=True)
    (OUT / "laughs.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    names = {"giggle": "хихиканье", "chuckle": "смешок носом", "laugh": "смех", "cackle": "заливается"}
    html = ["<!doctype html><meta charset='utf-8'><title>Смех Клавы</title>",
            "<style>body{font:14px Segoe UI,sans-serif;background:#16141f;color:#eee;margin:16px}td{padding:4px 8px;"
            "border-bottom:1px solid #333}h2{color:#7ff3ff}</style><h1>Смех Клавы — какие звучат живо?</h1>"
            "<p>Отметь номера живых (например «giggle_3, laugh_5») — их она будет вклеивать вместо «ха-ха». "
            "Тембр — близость к её голосу (0,9+ — она).</p>"]
    for kind, ru in names.items():
        html.append(f"<h2>{ru}</h2><table>")
        for r in [x for x in rows if x["kind"] == kind]:
            html.append(f"<tr><td>{r['file'][:-4]}</td><td><audio controls src='{r['file']}'></audio></td>"
                        f"<td>тембр {r['sim']:.2f}</td><td>{r['dur_s']} с</td></tr>")
        html.append("</table>")
    (WEB / "laughs.html").write_text("\n".join(html), encoding="utf-8")
    print("ГОТОВО", WEB / "laughs.html")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
