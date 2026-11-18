"""Судья звуков (нейросеть, одноразовая проверка): классификатор AudioSet (AST, MIT) слушает WAV и говорит, насколько это
похоже на «Burping, eructation» / «Fart» / «Crunch» / «Chewing, mastication». Нужен, чтобы подбирать синтез рыгов и пуков
не на глаз: сам послушать я не могу, а классификатор обучен на двух миллионах настоящих роликов.

    tts\.venv\Scripts\python.exe tools\sfx_judge.py папка_с_wav [--top 3]
"""
import sys, glob, os
import numpy as np, soundfile as sf, torch, librosa
from transformers import ASTFeatureExtractor, ASTForAudioClassification

MODEL = os.path.join(os.path.dirname(__file__), "..", "models", "audio", "ast-audioset")
if not os.path.isdir(MODEL):
    from huggingface_hub import snapshot_download
    snapshot_download("MIT/ast-finetuned-audioset-10-10-0.4593", local_dir=MODEL)
fe = ASTFeatureExtractor.from_pretrained(MODEL)
model = ASTForAudioClassification.from_pretrained(MODEL).eval()
labels = model.config.id2label
want = [k for k, v in labels.items() if v in ("Burping, eructation", "Fart", "Crunch", "Chewing, mastication", "Speech", "Laughter", "Crying, sobbing", "Sigh", "Groan", "Screaming", "Giggle", "Belly laugh", "Snicker", "Whimper", "Gasp", "Moo", "Growling")]
top = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 3
files = sorted(glob.glob(os.path.join(sys.argv[1], "*.wav"))) if os.path.isdir(sys.argv[1]) else sys.argv[1:2]
for f in files:
    x, sr = sf.read(f, dtype="float32")
    if x.ndim > 1: x = x.mean(1)
    x = librosa.resample(x, orig_sr=sr, target_sr=16000)
    with torch.no_grad():
        p = torch.sigmoid(model(**fe(x, sampling_rate=16000, return_tensors="pt")).logits)[0].numpy()
    best = np.argsort(-p)[:top]
    focus = " ".join(f"{labels[k]}={p[k]:.2f}" for k in want if p[k] > 0.05)
    print(f"{os.path.basename(f)}: " + ", ".join(f"{labels[i]} {p[i]:.2f}" for i in best) + (f" | {focus}" if focus else ""))
