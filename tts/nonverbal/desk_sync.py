"""Банки живых звуков — по тому, что друг оставил на рабочем столе, плюс отсев страшного и невпопад (одноразовая
обработка медиа + нейросети-судьи).

Друг слушает копии («ИИ кошкодевочка\\Эмоции\\<папка>\\NN.wav») и удаляет страшное и невпопад (29.09: «в некоторых
папках страшные и не в попад я поудалял — найди и поотсей ещё неправильных»). Здесь:
 1. каждая оставшаяся копия узнаётся по звуку среди tts/voices/nonverbal/*.wav (лог-мел, сжатый по времени, косинус);
    не узналась (копия из прошлого прогона конвертера) — сама копия заводится в банк как <вид>_desk_NN.wav, 24 кГц;
 2. всё оставленное слушает судья AudioSet (AST, MIT): звук должен узнаваться как свой вид (смех — Laughter/Giggle/…,
    плач — Crying/Whimper, вздох — Sigh/Breathing, визг — Squeal/Scream, «ах!» — Gasp) и не быть «страшным»
    (Screaming/Wail/Howl/Growling у смеха — то самое «банши») и не быть речью со словами (Speech сильнее своего вида —
    «слова» из прошлого отсева друга);
 3. тембр — к голосу Клавы (WavLM, klava_02): ниже 0,66 — чужой голос (из прошлого отсева друга: < 0,69 — мимо);
 4. помеченное другом в имени «убрать» — мимо.
Банки emotions.json переписываются (копия .bak_before_desk_sync), отчёт — «Отсев.txt» рядом с папками на столе.
Стоны и ахегао не трогаются: их друг велел делать только по референсу «Yamete Kudasai».

    tts\\.venv\\Scripts\\python.exe nonverbal\\desk_sync.py [--dry]
"""
from __future__ import annotations

import json
import os
import shutil
import sys
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
import torch

TTS = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TTS))
ROOT = TTS.parent
BANK = TTS / "voices" / "nonverbal"
DESK = Path(os.path.expanduser("~")) / "Desktop" / "ИИ кошкодевочка" / "Эмоции"
EMO = TTS / "emotions.json"
AST = ROOT / "models" / "audio" / "ast-audioset"
KLAVA = TTS / "voices" / "candidates" / "klava_02.wav"

# папка на столе → вид; свои классы AudioSet; «страшные» классы для этого вида
KINDS = {
    "Смех": ("laugh", ["Laughter", "Giggle", "Snicker", "Belly laugh", "Chuckle, chortle", "Baby laughter"], ["Screaming", "Wail, moan", "Howl", "Growling", "Crying, sobbing"]),
    "Плач": ("cry", ["Crying, sobbing", "Whimper", "Baby cry, infant cry", "Wail, moan"], ["Screaming", "Howl", "Growling", "Laughter"]),
    "Вздохи": ("sigh", ["Sigh", "Breathing", "Gasp", "Pant"], ["Screaming", "Growling", "Groan"]),
    "Визг": ("squeal", ["Squeal", "Screaming", "Whoop", "Yell", "Giggle", "Laughter"], ["Growling", "Howl", "Groan"]),
    "Ах! (удивление)": ("gasp", ["Gasp", "Breathing", "Sigh", "Whoop"], ["Screaming", "Growling", "Groan"]),
}
SPEECH = ["Speech", "Female speech, woman speaking", "Male speech, man speaking", "Child speech, kid speaking", "Babbling"]


def fingerprint(y: np.ndarray) -> np.ndarray:
    """Отпечаток без громкости и лёгкой обрезки: лог-мел 40 полос, 48 кадров, полосы без среднего, нормирован."""
    y, _ = librosa.effects.trim(y, top_db=40)
    m = np.log(librosa.feature.melspectrogram(y=y, sr=16000, n_mels=40, hop_length=160) + 1e-7)
    m = m[:, np.linspace(0, m.shape[1] - 1, 48).astype(int)]
    m = m - m.mean(axis=1, keepdims=True)
    v = m.flatten()
    return v / (np.linalg.norm(v) + 1e-9)


class Judge:
    def __init__(self):
        from transformers import ASTFeatureExtractor, ASTForAudioClassification
        self.fe = ASTFeatureExtractor.from_pretrained(str(AST))
        self.model = ASTForAudioClassification.from_pretrained(str(AST)).eval()
        self.idx = {v: k for k, v in self.model.config.id2label.items()}

    def probs(self, y16: np.ndarray) -> dict[str, float]:
        with torch.no_grad():
            p = torch.sigmoid(self.model(**self.fe(y16, sampling_rate=16000, return_tensors="pt")).logits)[0].numpy()
        return {k: float(p[i]) for k, i in self.idx.items()}


def main() -> int:
    dry = "--dry" in sys.argv
    import speaker_id
    speaker_id.load_encoder()
    klava_y, _ = librosa.load(str(KLAVA), sr=16000, mono=True)
    kemb = speaker_id.embed_audio(klava_y)
    kemb = kemb / np.linalg.norm(kemb)
    judge = Judge()

    bank_fp = {}
    for p in BANK.glob("*.wav"):
        y, _ = librosa.load(str(p), sr=16000, mono=True)
        bank_fp[p.name] = fingerprint(y)

    emo = json.loads(EMO.read_text(encoding="utf-8"))
    nv = emo.get("nonverbal", {})
    report = ["Отсев живых звуков — что осталось в банках и что убрано и почему.",
              "Оставлено то, что ты оставил в папках, минус то, что судья AudioSet счёл страшным, речью или не тем звуком,",
              "и чужой тембр (не похоже на голос Клавы). Стоны и ахегао не трогала — они только по референсу «Yamete».", ""]
    for folder, (kind, own, scary) in KINDS.items():
        d = DESK / folder
        if not d.is_dir():
            continue
        cands = {n: f for n, f in bank_fp.items() if n.startswith(kind + "_") and "_desk_" not in n}
        keep, drop = [], []
        for f in sorted(d.glob("*.wav")):
            y16, _ = librosa.load(str(f), sr=16000, mono=True)
            fp = fingerprint(y16)
            best = max(cands.items(), key=lambda kv: float(kv[1] @ fp), default=(None, None))
            sim = float(best[1] @ fp) if best[0] else 0.0
            if best[0] and sim > 0.8:
                name = best[0]
            else:
                # копия из прошлого прогона — её и берём (друг слушал именно её)
                name = f"{kind}_desk_{f.stem[:2]}.wav"
                if not dry:
                    y24, _ = librosa.load(str(f), sr=24000, mono=True)
                    sf.write(BANK / name, y24 / (np.abs(y24).max() + 1e-9) * 0.89, 24000, subtype="PCM_16")
            p = judge.probs(y16)
            own_p = max(p[c] for c in own)
            scary_p = max(p[c] for c in scary)
            speech_p = max(p[c] for c in SPEECH)
            e = speaker_id.embed_audio(y16)
            timbre = float(e @ kemb / (np.linalg.norm(e) + 1e-9))
            # судья AudioSet на переведённых Seed-VC звуках туговат (плач он почти не узнаёт), поэтому убираем только
            # по явным признакам: страшно, явно слова, явно чужой голос, помечено другом; слабый свой класс — только
            # вместе с чужеватым тембром
            why = []
            if "убрать" in f.stem.lower():
                why.append("ты сам пометил «убрать»")
            if scary_p > max(0.3, own_p):
                why.append(f"страшно ({max(scary, key=lambda c: p[c])} {scary_p:.2f})")
            if speech_p > max(0.6, own_p * 2):
                why.append(f"слышны слова (речь {speech_p:.2f})")
            if timbre < 0.6:
                why.append(f"чужой голос (тембр {timbre:.2f})")
            if own_p < 0.06 and timbre < 0.66:
                why.append(f"не похоже на {kind} и голос чужеват (свой класс {own_p:.2f}, тембр {timbre:.2f})")
            line = f"  {folder}/{f.name} → {name}: свой {own_p:.2f}, страшно {scary_p:.2f}, речь {speech_p:.2f}, тембр {timbre:.2f}"
            (drop if why else keep).append((name, line + (" — УБРАНО: " + "; ".join(why) if why else "")))
        nv[kind] = [n for n, _ in keep]
        report.append(f"{folder}: в банке {len(keep)}, убрано {len(drop)}")
        report += [l for _, l in keep] + [l for _, l in drop] + [""]
    text = "\n".join(report)
    print(text)
    if dry:
        return 0
    shutil.copy2(EMO, EMO.with_suffix(".json.bak_before_desk_sync"))
    emo["nonverbal"] = nv
    EMO.write_text(json.dumps(emo, ensure_ascii=False, indent=1), encoding="utf-8")
    (DESK / "Отсев.txt").write_text(text, encoding="utf-8-sig")
    print("emotions.json обновлён")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
