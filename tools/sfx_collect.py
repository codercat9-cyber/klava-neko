"""Живые рыги, пуки, хруст и чавк для Клавы (одноразовая обработка медиа + нейросеть-судья).

Синтез рыгов и пуков классификатор AudioSet (AST) не узнал ни разу («музыка», «взрыв»), поэтому берём настоящие записи:
freesound.org, только лицензия CC0 (общественное достояние), превью HQ (mp3 128 кбит/с). Длинные файлы режутся на
отдельные события по громкости, каждое событие слушает судья AST — остаются только те, что он уверенно узнаёт как
«Burping, eructation» / «Fart» / «Crunch» / «Chewing, mastication». Рыги сортируются по высоте (женские выше).

    tts\\.venv\\Scripts\\python.exe tools\\sfx_collect.py
Итог: web/voices/sfx/<вид>_NN.wav + web/voices/sfx/sfx.json (список для страницы) + data/sfx_raw/sources.json (откуда что)
и копии для прослушивания: Рабочий стол\\ИИ кошкодевочка\\Эмоции\\Рыги и пуки\\<Вид>\\NN.wav (удалённое там — выкинуть
из списка: tools\\sfx_collect.py --sync).
"""
from __future__ import annotations

import html
import json
import os
import re
import shutil
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
import torch
from transformers import ASTFeatureExtractor, ASTForAudioClassification

ROOT = Path(__file__).resolve().parent.parent
RAW = ROOT / "data" / "sfx_raw"
OUT = ROOT / "web" / "voices" / "sfx"
DESK = Path(os.path.expanduser("~")) / "Desktop" / "ИИ кошкодевочка" / "Эмоции" / "Рыги и пуки"
MODEL = ROOT / "models" / "audio" / "ast-audioset"
SR = 44100

KINDS = {
    "burp": {"label": "Burping, eructation", "folder": "Рыги", "min_p": 0.35, "keep": 24,
             "queries": ["burp", "female burp", "girl burp", "belch", "burping", "woman burp"]},
    "fart": {"label": "Fart", "folder": "Пуки", "min_p": 0.35, "keep": 20,
             "queries": ["fart", "farting", "fart sound", "funny fart"]},
    "crunch": {"label": "Crunch", "folder": "Хруст", "min_p": 0.15, "keep": 12,
               "queries": ["chips crunch", "eating chips", "crisps eating", "crunchy bite"]},
    "chew": {"label": "Chewing, mastication", "folder": "Чавк", "min_p": 0.2, "keep": 10,
             "queries": ["chewing", "eating chewing", "munching"]},
}
UA = {"User-Agent": "Mozilla/5.0 (KlavaDesk sfx collector)"}


def search(query: str, pages: int = 2) -> list[dict]:
    out = []
    for page in range(1, pages + 1):
        url = "https://freesound.org/search/?" + urllib.parse.urlencode(
            {"q": query, "f": 'license:"Creative Commons 0"', "s": "Downloads (most first)", "page": page})
        try:
            body = urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=40).read().decode("utf-8", "replace")
        except Exception as e:  # noqa: BLE001 — сеть: пропускаем страницу
            print(f"  поиск «{query}» стр.{page}: {e}")
            continue
        for block in re.findall(r'<div\s+class="bw-player"(.*?)tabindex', body, re.S):
            attr = dict(re.findall(r'data-([a-z0-9-]+)="([^"]*)"', block))
            if "mp3" not in attr:
                continue
            dur = float(attr.get("duration", "0") or 0)
            if dur <= 0 or dur > 12:
                continue
            out.append({"id": attr.get("sound-id"), "user": html.unescape(attr.get("username", "")),
                        "title": html.unescape(attr.get("title", "")), "duration": dur,
                        "mp3": attr["mp3"].replace("-lq.mp3", "-hq.mp3"),
                        "page": f"https://freesound.org/people/{attr.get('username', '')}/sounds/{attr.get('sound-id')}/"})
        time.sleep(1.0)
    return out


def fetch(item: dict, kind: str) -> Path | None:
    d = RAW / kind
    d.mkdir(parents=True, exist_ok=True)
    f = d / f"{item['id']}.mp3"
    if f.exists() and f.stat().st_size > 1000:
        return f
    try:
        data = urllib.request.urlopen(urllib.request.Request(item["mp3"], headers=UA), timeout=60).read()
    except Exception as e:  # noqa: BLE001
        print(f"  не скачалось {item['id']}: {e}")
        return None
    f.write_bytes(data)
    time.sleep(0.4)
    return f


def events(x: np.ndarray, sr: int) -> list[np.ndarray]:
    """Отдельные события по громкости: паузы > 0,25 с тише −35 дБ от пика режут файл."""
    hop = 256
    rms = librosa.feature.rms(y=x, frame_length=1024, hop_length=hop)[0]
    if rms.max() <= 0:
        return []
    db = 20 * np.log10(rms / rms.max() + 1e-9)
    loud = db > -35
    segs, start, quiet = [], None, 0
    for i, on in enumerate(loud):
        if on:
            if start is None:
                start = i
            quiet = 0
        elif start is not None:
            quiet += 1
            if quiet * hop / sr > 0.25:
                segs.append((start, i - quiet + 1))
                start, quiet = None, 0
    if start is not None:
        segs.append((start, len(loud)))
    out = []
    for a, b in segs:
        s0, s1 = max(0, a * hop - int(0.03 * sr)), min(len(x), b * hop + int(0.08 * sr))
        seg = x[s0:s1]
        if 0.15 <= len(seg) / sr <= 3.0:
            fade = int(0.01 * sr)
            seg = seg.copy()
            seg[:fade] *= np.linspace(0, 1, fade)
            seg[-fade:] *= np.linspace(1, 0, fade)
            out.append(seg)
    return out


class Judge:
    def __init__(self):
        if not MODEL.is_dir():
            from huggingface_hub import snapshot_download
            snapshot_download("MIT/ast-finetuned-audioset-10-10-0.4593", local_dir=str(MODEL))
        self.fe = ASTFeatureExtractor.from_pretrained(str(MODEL))
        self.model = ASTForAudioClassification.from_pretrained(str(MODEL)).eval()
        self.idx = {v: k for k, v in self.model.config.id2label.items()}

    def __call__(self, x: np.ndarray, sr: int, label: str) -> float:
        y = librosa.resample(x, orig_sr=sr, target_sr=16000)
        with torch.no_grad():
            p = torch.sigmoid(self.model(**self.fe(y, sampling_rate=16000, return_tensors="pt")).logits)[0]
        return float(p[self.idx[label]])


def pitch(x: np.ndarray, sr: int) -> float:
    f0, voiced, _ = librosa.pyin(librosa.resample(x, orig_sr=sr, target_sr=16000), fmin=50, fmax=700, sr=16000)
    f0 = f0[voiced] if voiced is not None else np.array([])
    return float(np.nanmedian(f0)) if f0.size else 0.0


def collect() -> None:
    judge = Judge()
    OUT.mkdir(parents=True, exist_ok=True)
    manifest, sources = {}, {}
    for kind, spec in KINDS.items():
        seen, found = set(), []
        for q in spec["queries"]:
            for it in search(q):
                if it["id"] in seen:
                    continue
                seen.add(it["id"])
                found.append(it)
        print(f"{kind}: {len(found)} файлов CC0")
        cands = []
        for it in found:
            f = fetch(it, kind)
            if f is None:
                continue
            try:
                x, sr = librosa.load(str(f), sr=SR, mono=True)
            except Exception as e:  # noqa: BLE001
                print(f"  не читается {f.name}: {e}")
                continue
            for k, seg in enumerate(events(x, sr)):
                p = judge(seg, sr, spec["label"])
                if p >= spec["min_p"]:
                    cands.append({"p": p, "seg": seg, "src": it, "part": k})
        # лучшие по уверенности судьи; не больше трёх событий из одного файла — разнообразие
        cands.sort(key=lambda c: -c["p"])
        per, chosen = {}, []
        for c in cands:
            if per.get(c["src"]["id"], 0) >= 3:
                continue
            per[c["src"]["id"]] = per.get(c["src"]["id"], 0) + 1
            chosen.append(c)
            if len(chosen) >= spec["keep"]:
                break
        if kind == "burp":
            for c in chosen:
                c["f0"] = pitch(c["seg"], SR)
            chosen.sort(key=lambda c: -c["f0"])        # выше (женственнее) — первыми
        names = []
        for i, c in enumerate(chosen):
            seg = c["seg"] / (np.abs(c["seg"]).max() + 1e-9) * 0.89
            name = f"{kind}_{i:02d}"
            sf.write(OUT / f"{name}.wav", seg, SR, subtype="PCM_16")
            names.append(name)
            sources[name] = {**c["src"], "part": c["part"], "p": round(c["p"], 3), "f0": round(c.get("f0", 0), 1), "license": "CC0"}
        manifest[kind] = names
        print(f"{kind}: оставлено {len(names)} (судья ≥ {spec['min_p']}): " + ", ".join(f"{sources[n]['p']:.2f}" for n in names))
    (OUT / "sfx.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
    RAW.mkdir(parents=True, exist_ok=True)
    (RAW / "sources.json").write_text(json.dumps(sources, ensure_ascii=False, indent=1), encoding="utf-8")
    export(manifest)


def export(manifest: dict) -> None:
    """Копии для прослушивания: <Вид>/NN.wav, карта номеров — в data/sfx_raw/desk_map.json."""
    if DESK.exists():
        shutil.rmtree(DESK)
    mapping = {}
    for kind, names in manifest.items():
        d = DESK / KINDS[kind]["folder"]
        d.mkdir(parents=True, exist_ok=True)
        for i, n in enumerate(names, 1):
            shutil.copy2(OUT / f"{n}.wav", d / f"{i:02d}.wav")
            mapping[f"{KINDS[kind]['folder']}/{i:02d}.wav"] = n
    (RAW / "desk_map.json").write_text(json.dumps(mapping, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"для прослушивания: {DESK}")


def sync() -> None:
    """Друг удалил на рабочем столе неудачные — убрать их из списка страницы."""
    mapping = json.loads((RAW / "desk_map.json").read_text(encoding="utf-8"))
    manifest = json.loads((OUT / "sfx.json").read_text(encoding="utf-8"))
    gone = {name for rel, name in mapping.items() if not (DESK / rel).exists()}
    for kind in manifest:
        manifest[kind] = [n for n in manifest[kind] if n not in gone]
    (OUT / "sfx.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"убрано из списка: {len(gone)}; осталось: " + ", ".join(f"{k} {len(v)}" for k, v in manifest.items()))


if __name__ == "__main__":
    sync() if "--sync" in sys.argv else collect()
