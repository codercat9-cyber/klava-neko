"""Как Клава звучит в каждой эмоции — через живой сервер голоса (server.py с tts/emotions.json).

    python emotion_demo.py [--url http://127.0.0.1:8095]

Кладёт web/voices/emotions/demo_<эмоция>.wav и вставляет раздел «Как она будет звучать» в начало emotions.html.
"""
import argparse
import json
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web" / "voices" / "emotions"

# реплики в характере: одна и та же мысль по-разному плюс своя фраза эмоции
LINES = {
    "calm": "Ну привет. Как день прошёл, рассказывай.",
    "happy": "Ура, получилось! Я же говорила, что мы справимся, мур!",
    "laugh": "Ха-ха, ну ты выдал! Ой, не могу, это просто огонь!",
    "angry": "Ты опять удалил мой файл?! Сколько можно, я же просила!",
    "annoyed": "Пфф, ну вот опять. Ладно, сделаю, но это последний раз.",
    "sad": "Эх, ты сегодня совсем со мной не разговариваешь...",
    "surprised": "Ого! Серьёзно? Вот это поворот, не ожидала!",
    "shy": "Ой, ну хватит так смотреть, я же вся краснею...",
    "tender": "Мурр, иди сюда. Я так по тебе соскучилась.",
    "sly": "Ну конечно, ты у нас самый умный. Ага, верю-верю.",
    "whisper": "Тс-с, только никому не говори. Это наш секрет.",
    "scared": "Ой! Что это там шуршит? Мне страшно, не уходи никуда!",
    "cry": "Ну почему всё так... я же правда старалась, а оно опять не получилось.",
    "sigh": "Ладно, давай ещё разок попробуем, только не торопись.",
    "sultry": "Ну иди сюда, чего ты там стоишь такой серьёзный?",
    "moan": "Мр-р, вот так хорошо, погладь ещё за ушком.",
}
NAMES = {"calm": "спокойствие", "happy": "радость", "laugh": "смех", "angry": "злость", "annoyed": "раздражение", "sad": "грусть",
         "surprised": "удивление", "shy": "смущение", "tender": "нежность", "sly": "хитрость", "whisper": "шёпот",
         "scared": "испуг", "cry": "плач", "sigh": "вздох", "sultry": "томно", "moan": "стон"}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:8095")
    a = ap.parse_args()
    WEB.mkdir(parents=True, exist_ok=True)
    rows = []
    for emo, text in LINES.items():
        body = json.dumps({"text": text, "emotion": emo}).encode("utf-8")
        req = urllib.request.Request(a.url + "/tts", data=body, headers={"Content-Type": "application/json"})
        t = time.time()
        wav = urllib.request.urlopen(req, timeout=120).read()
        (WEB / f"demo_{emo}.wav").write_bytes(wav)
        rows.append((emo, text, time.time() - t))
        print(f"{emo}: {len(wav) / 48000:.1f} с аудио за {time.time() - t:.1f} с", flush=True)
    page = WEB / "emotions.html"
    old = page.read_text(encoding="utf-8") if page.exists() else ""
    marker = "<h1>Голос Клавы: эмоции</h1>"
    demo = [marker, "<h2>Как она будет звучать (живой голос, tts/emotions.json)</h2><table>"]
    for emo, text, _ in rows:
        demo.append(f"<tr><td>{NAMES[emo]}</td><td><audio controls src='demo_{emo}.wav'></audio></td><td>{text}</td></tr>")
    demo.append("</table><h2 style='color:#aaa'>Ниже — все кандидаты, из которых выбраны эталоны</h2>")
    if marker in old:
        before, after = old.split(marker, 1)
        # прежний демо-раздел (если был) выкидываем — до строки «Ниже — все кандидаты…»
        if "Ниже — все кандидаты" in after:
            after = after.split("Ниже — все кандидаты", 1)[1].split("</h2>", 1)[1]
        page.write_text(before + "\n".join(demo) + after, encoding="utf-8")
    else:
        page.write_text("<!doctype html><meta charset='utf-8'>" + "\n".join(demo), encoding="utf-8")
    print("страница:", page)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
