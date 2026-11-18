"""Проверка смеха внутри речи через живой сервер голоса (одноразовая, замеры — нейросеть WavLM для тембра).

Одни и те же фразы: спокойно (calm), со смехом по метке в начале, со смехом посреди фразы («ха-ха» → 🤣) — сколько времени
добавил смех, есть ли в нём смеховые всплески (4–7 Гц огибающей), осталась ли она собой (WavLM к klava_02). Страница для
уха друга: web/voices/emotions/laugh2/laugh2.html.

    tts\\.venv\\Scripts\\python.exe emo_train\\laugh_check.py
"""
import io
import json
import sys
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf

TTS = Path(__file__).resolve().parent.parent
ROOT = TTS.parent
sys.path.insert(0, str(TTS))
import speaker_id  # noqa: E402
from emotion_voice import REF, to16k  # noqa: E402
from server import median_f0  # noqa: E402

URL = "http://127.0.0.1:8095"
WEB = ROOT / "web" / "voices" / "emotions" / "laugh2"
LINES = [
    ("Ну ты выдал, Кибер Кот, я сейчас со стула упаду!", "Ну ты выдал, ха-ха-ха, Кибер Кот, я сейчас со стула упаду!"),
    ("Ты серьёзно это нарисовал? Это же не яблоко, а картошка!", "Ты серьёзно это нарисовал? Ахаха, это же не яблоко, а картошка!"),
    ("Ладно, признаю, это было очень смешно.", "Ладно, ха-ха, признаю, это было очень смешно."),
    ("Слушай, а помнишь, как ты в Зуме лягушку утопил?", "Слушай, а помнишь, хи-хи, как ты в Зуме лягушку утопил?"),
]


def tts(text: str, emotion: str) -> tuple[np.ndarray, int]:
    req = urllib.request.Request(URL + "/tts", data=json.dumps({"text": text, "emotion": emotion}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    w, sr = sf.read(io.BytesIO(urllib.request.urlopen(req, timeout=180).read()), dtype="float32")
    return w, sr


def bursts(w: np.ndarray, sr: int) -> float:
    """Доля энергии огибающей в полосе 4–7 Гц — ритм «ха-ха-ха» (у речи слоги ~4–6 Гц тоже, поэтому — в сравнении)."""
    fr = int(sr * 0.01)
    n = len(w) // fr
    env = np.sqrt(np.mean(w[: n * fr].reshape(n, fr) ** 2, axis=1))
    env = env - env.mean()
    spec = np.abs(np.fft.rfft(env)) ** 2
    f = np.fft.rfftfreq(len(env), 0.01)
    return float(spec[(f >= 4) & (f <= 7)].sum() / (spec[(f >= 1) & (f <= 15)].sum() + 1e-9))


def main() -> int:
    WEB.mkdir(parents=True, exist_ok=True)
    speaker_id.load_encoder()
    rw, rs = sf.read(REF, dtype="float32")
    remb = speaker_id.embed_audio(to16k(rw, rs))
    rows = []
    for i, (plain, haha) in enumerate(LINES):
        # laugh_start — как сервер сам ставит метку без «ха-ха»: после первого оборота
        comma = plain.find(",")
        auto = f"{plain[:comma]} 🤣{plain[comma:]}" if 0 < comma < len(plain) * 0.5 else " ".join(plain.split(" ")[:2] + ["🤣"] + plain.split(" ")[2:])
        for kind, text, emo in (("calm", plain, "calm"), ("laugh_start", auto, "laugh"), ("laugh_mid", haha, "laugh")):
            w, sr = tts(text, emo)
            e = speaker_id.embed_audio(to16k(w, sr))
            sim = float(np.dot(remb, e) / (np.linalg.norm(remb) * np.linalg.norm(e) + 1e-9))
            name = f"{i}_{kind}.wav"
            sf.write(WEB / name, w, sr)
            row = {"file": name, "kind": kind, "text": text, "dur": round(len(w) / sr, 2), "sim": round(sim, 3),
                   "f0": round(median_f0(w, sr)), "bursts": round(bursts(w, sr), 3)}
            rows.append(row)
            print(json.dumps(row, ensure_ascii=False), flush=True)
    html = ["<!doctype html><meta charset='utf-8'><title>Смех внутри речи</title>",
            "<style>body{font:14px Segoe UI,sans-serif;background:#16141f;color:#eee;margin:16px}td{padding:4px 8px;border-bottom:1px solid #333}"
            "h2{color:#7ff3ff}</style><h1>Смех внутри речи — обучено на живом смехе (NonverbalTTS)</h1>",
            "<p>Каждая фраза трижды: спокойно, со смехом в начале, со смехом посреди (где было «ха-ха»).</p><table>"]
    names = {"calm": "спокойно", "laugh_start": "смех сам (метка после первого оборота)", "laugh_mid": "смех на месте «ха-ха»"}
    for r in rows:
        html.append(f"<tr><td>{names[r['kind']]}</td><td><audio controls src='{r['file']}'></audio></td><td>{r['text']}</td>"
                    f"<td>{r['dur']} с</td><td>тембр {r['sim']:.2f}</td><td>{r['f0']} Гц</td></tr>")
    html.append("</table>")
    (WEB / "laugh2.html").write_text("\n".join(html), encoding="utf-8")
    (WEB / "laugh2.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    print("страница:", WEB / "laugh2.html")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
