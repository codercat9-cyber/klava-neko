"""Сервер голоса Клавы: Qwen3-TTS 0.6B Base клонирует тембр, собранный в design_voice.py.

Движок — faster-qwen3-tts (MIT): статический KV-кэш + torch.cuda.CUDAGraph. Обычный qwen-tts
на этой машине давал RTF ~7: каждая из тысяч мелких операций на кадр упиралась в запуск ядра
CUDA под WDDM, а записанный граф запускается одной командой.

    POST /tts     {"text": "..."}  -> audio/wav (PCM 16 бит, моно)
    GET  /health                   -> 200, когда модель загружена и прогрета

Эталон голоса — tts/voice.json: {"ref": "<путь к wav>", "text": "<что в нём сказано>"}.
Видеокарта одна: запросы выполняются по одному под замком.

    python server.py [--port 8095] [--bench]
"""
import argparse
import io
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from faster_qwen3_tts import FasterQwen3TTS
from stress import apply_stress

ROOT = Path(__file__).resolve().parent.parent
MODEL = ROOT / "models" / "Qwen3-TTS-12Hz-0.6B-Base"
VOICE = ROOT / "tts" / "voice.json"

_lock = threading.Lock()
_model = None
_ref = ""
_ref_text = ""
_ready = False


def log(msg: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def load() -> None:
    global _model, _ref, _ref_text, _ready
    voice = json.loads(VOICE.read_text(encoding="utf-8"))
    ref = Path(voice["ref"])
    if not ref.is_absolute():
        ref = ROOT / ref
    _ref, _ref_text = str(ref), voice["text"]
    t = time.time()
    _model = FasterQwen3TTS.from_pretrained(
        # 1024 позиций статического кэша хватает на реплику ~40 с (эталон ~10 с + текст + 12 кадров/с);
        # 2048 отняли бы ещё ~120 МБ видеопамяти, которой рядом с FlashNext впритык
        str(MODEL), device="cuda", dtype=torch.bfloat16, attn_implementation="sdpa", max_seq_len=1024)
    log(f"модель загружена за {time.time() - t:.1f} с, VRAM {torch.cuda.memory_allocated() / 2**30:.2f} ГиБ, "
        f"эталон {ref.name}")
    t = time.time()
    synth("Мур.")  # первый вызов записывает CUDA-графы и кэширует эталон голоса
    log(f"прогрев (запись графов) {time.time() - t:.1f} с")
    _ready = True


def synth(text: str) -> tuple[np.ndarray, int, float]:
    stressed_text, changes = apply_stress(text)
    if changes:
        log(f"ударения: {', '.join(changes[:6])}{'...' if len(changes) > 6 else ''}")
    with _lock:
        t = time.time()
        wavs, sr = _model.generate_voice_clone(text=stressed_text, language="Russian", ref_audio=_ref, ref_text=_ref_text)
        took = time.time() - t
    wav = np.asarray(wavs[0], dtype=np.float32)
    dur = len(wav) / sr
    log(f"синтез {len(text)} симв -> {dur:.2f} с аудио за {took:.2f} с (RTF {took / max(dur, 1e-3):.2f})")
    return wav, sr, took


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):  # свой лог вместо стандартного шума
        pass

    def _send(self, code: int, body: bytes, ctype: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send(200 if _ready else 503, b"ok" if _ready else b"loading", "text/plain")
        else:
            self._send(404, b"not found", "text/plain")

    def do_POST(self):
        if self.path != "/tts":
            self._send(404, b"not found", "text/plain")
            return
        if not _ready:
            self._send(503, b"loading", "text/plain")
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            req = json.loads(self.rfile.read(length).decode("utf-8"))
            text = str(req.get("text", "")).strip()
            if not text:
                self._send(400, b"empty text", "text/plain")
                return
            wav, sr, _ = synth(text[:600])
            buf = io.BytesIO()
            sf.write(buf, wav, sr, format="WAV", subtype="PCM_16")
            self._send(200, buf.getvalue(), "audio/wav")
        except Exception as e:  # клиенту — текст ошибки, серверу — жить дальше
            log(f"ошибка: {e!r}")
            self._send(500, str(e).encode("utf-8"), "text/plain; charset=utf-8")


def bench() -> None:
    phrases = [
        "Приветик! Как у тебя дела?",
        "Мур, я немножко задумалась, но уже тут.",
        "Слушай, а давай сегодня сделаем что-нибудь классное? Я так рада тебя видеть!",
    ]
    total_audio = total_gen = 0.0
    for p in phrases:
        wav, sr, took = synth(p)
        total_audio += len(wav) / sr
        total_gen += took
    log(f"ИТОГ: {total_audio:.2f} с аудио за {total_gen:.2f} с, RTF {total_gen / total_audio:.2f}, "
        f"пик VRAM {torch.cuda.max_memory_allocated() / 2**30:.2f} ГиБ")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8095)
    ap.add_argument("--bench", action="store_true")
    args = ap.parse_args()
    load()
    if args.bench:
        bench()
        return
    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    log(f"голос Клавы слушает http://127.0.0.1:{args.port}")
    srv.serve_forever()


if __name__ == "__main__":
    sys.exit(main())
