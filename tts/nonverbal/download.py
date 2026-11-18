"""Скачать всё для живых смеха, плача, вздохов и стонов Клавы (друг 28.09 разрешил: ASVP-ESD, Seed-VC, NonverbalTTS).

    tts\\.venv\\Scripts\\python.exe tts\\nonverbal\\download.py

 - ASVP-ESD (Zenodo 7132783, CC BY 4.0, 1,7 ГБ): живые смех, плач, вздохи, стоны → data/emotion/asvp/
 - веса Seed-VC (перевод голоса в тембр Клавы без обучения) → кэш HF в models/hf_cache (HF_HOME)
 - NonverbalTTS (deepvk, Apache-2.0, 4,2 ГБ): речь со смехом и вздохами внутри фраз → data/emotion/nvtts/
Докачивает оборванное; уже скачанное пропускает.
"""
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
os.environ.setdefault("HF_HOME", str(ROOT / "models" / "hf_cache"))
from huggingface_hub import hf_hub_download, snapshot_download  # noqa: E402


def log(msg: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def asvp() -> None:
    dst = ROOT / "data" / "emotion" / "asvp" / "ASVP-ESD-Update.zip"
    dst.parent.mkdir(parents=True, exist_ok=True)
    url = "https://zenodo.org/records/7132783/files/ASVP-ESD-Update.zip?download=1"
    for attempt in range(5):
        r = subprocess.run(["curl", "-L", "-C", "-", "--retry", "5", "-sS", "-o", str(dst), url])
        if r.returncode in (0, 33) and dst.exists() and dst.stat().st_size > 1_500_000_000:
            log(f"ASVP-ESD: {dst.stat().st_size / 2**30:.2f} ГиБ")
            return
        log(f"ASVP-ESD: попытка {attempt + 1} — curl {r.returncode}, докачиваю")
        time.sleep(5)
    raise RuntimeError("ASVP-ESD не скачался")


def seedvc() -> None:
    files = [
        ("Plachta/Seed-VC", "DiT_seed_v2_uvit_whisper_small_wavenet_bigvgan_pruned.pth"),
        ("Plachta/Seed-VC", "config_dit_mel_seed_uvit_whisper_small_wavenet.yml"),
        ("funasr/campplus", "campplus_cn_common.bin"),
        ("nvidia/bigvgan_v2_22khz_80band_256x", "bigvgan_generator.pt"),
        ("nvidia/bigvgan_v2_22khz_80band_256x", "config.json"),
    ]
    for repo, name in files:
        p = hf_hub_download(repo, name)
        log(f"Seed-VC: {repo}/{name} → {Path(p).stat().st_size / 2**20:.0f} МиБ")
    p = snapshot_download("openai/whisper-small", allow_patterns=["*.json", "model.safetensors", "*.txt"])
    log(f"Seed-VC: whisper-small → {p}")


def seedvc_f0() -> None:
    """Seed-VC с тоном (друг 28.09: без тона смех вышел «обезьяньим урчанием» — модель придумывала тон сама):
    DiT whisper-base f0 44 кГц, whisper-base, BigVGAN 44 кГц, RMVPE (тон)."""
    files = [
        ("Plachta/Seed-VC", "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth"),
        ("Plachta/Seed-VC", "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml"),
        ("nvidia/bigvgan_v2_44khz_128band_512x", "bigvgan_generator.pt"),
        ("nvidia/bigvgan_v2_44khz_128band_512x", "config.json"),
        ("lj1995/VoiceConversionWebUI", "rmvpe.pt"),
    ]
    for repo, name in files:
        p = hf_hub_download(repo, name)
        log(f"Seed-VC f0: {repo}/{name} → {Path(p).stat().st_size / 2**20:.0f} МиБ")
    p = snapshot_download("openai/whisper-base", allow_patterns=["*.json", "model.safetensors", "*.txt"])
    log(f"Seed-VC f0: whisper-base → {p}")


def nvtts() -> None:
    p = snapshot_download("deepvk/NonverbalTTS", repo_type="dataset", local_dir=str(ROOT / "data" / "emotion" / "nvtts"))
    size = sum(f.stat().st_size for f in Path(p).rglob("*") if f.is_file())
    log(f"NonverbalTTS: {size / 2**30:.2f} ГиБ → {p}")


if __name__ == "__main__":
    what = sys.argv[1:] or ["seedvc", "asvp", "nvtts"]
    for w in what:
        log(f"качаю {w}")
        {"asvp": asvp, "seedvc": seedvc, "seedvc_f0": seedvc_f0, "nvtts": nvtts}[w]()
    log("ГОТОВО")
