"""Silero + RVC(Клава) TTS-сервер: русский язык с правильными ударениями + голос Клавы.

Пайплайн:
  1. Текст → stress.apply_stress (RUAccent + custom_stress.json → `+гласная`).
  2. Silero v4_ru синтезирует речь с ударениями (baya/xenia/kseniya/aidar/eugene).
  3. RVC-модель Клавы (обучена в Applio на 6 её эталонах) переклоняет тембр — получаем клавин голос.
  4. Возвращаем WAV клиенту.

Тот же интерфейс, что у Qwen-сервера: POST /tts, GET /health.
Работает в venv Applio (C:\\PROG\\Applio\\env\\python.exe) — там уже есть torch, RVC, contentvec.

    python server_silero.py [--port 8095] [--speaker xenia] [--pitch 0]
"""
import argparse
import io
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

# Applio живёт своим корнем — оттуда импортируется VoiceConverter (rvc.infer.infer)
APPLIO = Path(r"C:\PROG\Applio")
sys.path.insert(0, str(APPLIO))

import numpy as np
import soundfile as sf
import torch

from stress import apply_stress, reload_custom_stress  # плюсы Silero понимает нативно

ROOT = Path(__file__).resolve().parent.parent
SILERO_MODEL = ROOT / "models" / "silero_v4_ru.pt"
KLAVA_LOGS = APPLIO / "logs" / "klava"

_lock = threading.Lock()
_silero = None
_rvc = None
_klava_pth: str | None = None
_klava_index: str | None = None
_speaker = "xenia"
_pitch = 0   # полутоны, +/-12; RVC-модели тренированной на женском голосе не нужен сдвиг
_ready = False


def log(msg: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def _find_klava_weights() -> tuple[str, str]:
    """Найти последний .pth и .index обученной Клавы. Ищем самый свежий weights-файл."""
    if not KLAVA_LOGS.exists():
        raise FileNotFoundError(f"нет папки {KLAVA_LOGS} — сначала обучи RVC-модель Клавы")
    # Файлы весов: klava_<epoch>e_<step>s.pth, лежат прямо в logs/klava/
    weights = sorted(KLAVA_LOGS.glob("klava_*.pth"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not weights:
        raise FileNotFoundError(f"в {KLAVA_LOGS} нет klava_*.pth — тренировка не закончилась?")
    index_files = sorted(KLAVA_LOGS.glob("*.index"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not index_files:
        raise FileNotFoundError(f"в {KLAVA_LOGS} нет .index — extract или train упал?")
    return str(weights[0]), str(index_files[0])


def load() -> None:
    global _silero, _rvc, _klava_pth, _klava_index, _ready

    log("Silero загружается…")
    t = time.time()
    _silero = torch.package.PackageImporter(str(SILERO_MODEL)).load_pickle("tts_models", "model")
    _silero.to(torch.device("cuda" if torch.cuda.is_available() else "cpu"))
    log(f"Silero готов за {time.time() - t:.1f} с. Голоса: {_silero.speakers}")

    log("RVC загружается…")
    t = time.time()
    from rvc.infer.infer import VoiceConverter   # импорт после sys.path.insert(APPLIO)
    _rvc = VoiceConverter()
    _klava_pth, _klava_index = _find_klava_weights()
    log(f"RVC готов за {time.time() - t:.1f} с. Клавины веса: {Path(_klava_pth).name}, индекс: {Path(_klava_index).name}")

    # Прогрев — первый синтез компилирует граф CUDA, следующие идут быстрее.
    log("прогрев…")
    _synth("Мур, я готова.")
    _ready = True
    log("готова.")


def _synth(text: str) -> tuple[np.ndarray, int]:
    """Silero → RVC-конверсия → PCM float32 моно 48 кГц."""
    stressed, changes = apply_stress(text)
    if changes:
        log(f"ударения/транслит: {', '.join(changes[:6])}{'...' if len(changes) > 6 else ''}")

    # Silero → 48 кГц wav (Silero отдаёт torch tensor)
    silero_wav = _silero.apply_tts(text=stressed, speaker=_speaker, sample_rate=48000, put_accent=True, put_yo=True)
    if isinstance(silero_wav, torch.Tensor):
        silero_wav = silero_wav.cpu().numpy()

    # Сохраняем во временный файл и прогоняем через RVC — VoiceConverter принимает пути
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as fin:
        sf.write(fin.name, silero_wav, 48000, subtype="PCM_16")
        src_path = fin.name
    dst_path = src_path.replace(".wav", "_klava.wav")
    try:
        _rvc.convert_audio(
            pitch=_pitch,
            index_rate=0.6,
            volume_envelope=1.0,
            protect=0.33,
            f0_method="rmvpe",
            audio_input_path=src_path,
            audio_output_path=dst_path,
            model_path=_klava_pth,
            index_path=_klava_index,
            split_audio=False,
            f0_autotune=False,
            f0_autotune_strength=1.0,
            proposed_pitch=False,
            proposed_pitch_threshold=0.0,
            clean_audio=False,
            clean_strength=0.0,
            export_format="WAV",
            embedder_model="contentvec",
            sid=0,
        )
        out_wav, sr = sf.read(dst_path, dtype="float32")
        if out_wav.ndim > 1:
            out_wav = out_wav.mean(axis=1)
        return out_wav.astype(np.float32), int(sr)
    finally:
        for p in (src_path, dst_path):
            try: Path(p).unlink(missing_ok=True)
            except OSError: pass


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args): pass

    def _send(self, code: int, body: bytes, ctype: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send(200 if _ready else 503, b"ok" if _ready else b"loading", "text/plain")
        elif self.path == "/reload_stress":
            n = reload_custom_stress()
            self._send(200, f"custom_stress reloaded: {n} words".encode("utf-8"), "text/plain")
        else:
            self._send(404, b"not found", "text/plain")

    def do_POST(self):
        if self.path != "/tts":
            self._send(404, b"not found", "text/plain"); return
        if not _ready:
            self._send(503, b"loading", "text/plain"); return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            req = json.loads(self.rfile.read(length).decode("utf-8"))
            text = str(req.get("text", "")).strip()
            if not text:
                self._send(400, b"empty text", "text/plain"); return
            with _lock:
                t = time.time()
                wav, sr = _synth(text[:600])
                took = time.time() - t
            dur = len(wav) / sr
            log(f"синтез {len(text)} симв → {dur:.2f} с аудио за {took:.2f} с (RTF {took / max(dur, 1e-3):.2f})")
            buf = io.BytesIO()
            sf.write(buf, wav, sr, format="WAV", subtype="PCM_16")
            self._send(200, buf.getvalue(), "audio/wav")
        except Exception as e:
            log(f"ошибка: {e!r}")
            try: self._send(500, str(e).encode("utf-8"), "text/plain; charset=utf-8")
            except (ConnectionAbortedError, BrokenPipeError): pass


def main() -> None:
    global _speaker, _pitch
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8095)
    ap.add_argument("--speaker", type=str, default="xenia",
                    help="Silero-голос перед RVC: xenia (мягкий), baya, kseniya, aidar, eugene")
    ap.add_argument("--pitch", type=int, default=0, help="Сдвиг питча RVC в полутонах, +/-12")
    args = ap.parse_args()
    _speaker = args.speaker
    _pitch = args.pitch
    load()
    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    log(f"Silero+RVC(klava) слушает http://127.0.0.1:{args.port}  (speaker={_speaker}, pitch={_pitch})")
    srv.serve_forever()


if __name__ == "__main__":
    sys.exit(main())
