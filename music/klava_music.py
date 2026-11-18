"""Музыкальный слух и пение Клавы — локальный сервис (KlavaDesk поднимает его сам, когда нужна музыка).

Задачи тяжёлые (минуты, видеокарта), поэтому очередь: одна задача за раз, прогресс и отмена.

    POST /job      {"kind": "analyze"|"plan"|"check"|"sing", "path": трек, ...}  → {"id"}
    GET  /job/<id> → {"state": queued|running|done|error|cancelled, "progress": 0..1, "message", "result"}
    POST /cancel/<id>
    GET  /health

analyze: quick (без Basic Pitch), language, force; plan — лист для перевода; check — lyrics (строки) → проверка слогов
и ударений; sing — lyrics (строки; «=» — оригинал её тембром, "" — промолчать) или orig=true.
Русский кавер — живым вокалом ACE-Step (ace_cover.py), engine="world" — прежний путь (речь по нотам).
Простой 30 минут без задач — выход (память и видеокарта нужнее голосу и мозгу).

    tts\\.venv\\Scripts\\python.exe music\\klava_music.py --port 8099 [--stt URL] [--tts URL]
"""
from __future__ import annotations

import argparse
import json
import os
import queue
import sys
import threading
import time
import traceback
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import common as C  # noqa: E402

STT = "http://127.0.0.1:8097"
TTS = "http://127.0.0.1:8095"
IDLE_EXIT = 30 * 60


class Cancelled(Exception):
    pass


class Job:
    def __init__(self, kind: str, args: dict):
        self.id = uuid.uuid4().hex[:12]
        self.kind, self.args = kind, args
        self.state, self.progress, self.message = "queued", 0.0, "в очереди"
        self.result: dict | list | None = None
        self.cancel = False
        self.created = time.time()

    def view(self) -> dict:
        return {"id": self.id, "kind": self.kind, "state": self.state, "progress": round(self.progress, 3),
                "message": self.message, "result": self.result}


JOBS: dict[str, Job] = {}
Q: "queue.Queue[Job]" = queue.Queue()
LOCK = threading.Lock()
_last = time.time()


def touch() -> None:
    global _last
    _last = time.time()


def run(job: Job) -> None:
    def progress(p: float, msg: str) -> None:
        if job.cancel:
            raise Cancelled()
        job.progress, job.message = max(job.progress, float(p)), msg
        C.log(f"[{job.kind} {job.id}] {int(p * 100):3d}% {msg}")
        touch()

    import analyze as A
    import cover as CV
    a = job.args
    path = str(a.get("path", ""))
    if job.kind in ("analyze", "plan", "sing", "check") and not (Path(path).is_file() or Path(path).is_dir()):
        raise FileNotFoundError(f"нет файла {path}")
    stt = a.get("stt") or STT
    tts = a.get("tts") or TTS
    if job.kind == "analyze":
        an = A.analyze(path, progress=progress, quick=bool(a.get("quick")), stt_url=stt, language=a.get("language") or None,
                       force=bool(a.get("force")))
        d = Path(an["dir"])
        job.result = {"dir": str(d), "passport": (d / "passport.txt").read_text(encoding="utf-8"),
                      "title": an.get("meta", {}).get("title") or Path(an["source"]).stem, "artist": an.get("meta", {}).get("artist", ""),
                      "duration": an["duration"], "sung": an.get("sung"), "bpm": an["tempo"].get("bpm"),
                      "key": an.get("key", {}).get("name"), "language": an.get("lyrics", {}).get("language", ""),
                      "lines": len(an.get("lyrics", {}).get("lines", []))}
    elif job.kind == "plan":
        plan = CV.make_plan(path, progress=progress, stt_url=stt)
        d = Path(plan["track"])
        job.result = {"dir": str(d), "sheet": (d / "translate.txt").read_text(encoding="utf-8"), "title": plan["title"],
                      "artist": plan.get("artist", ""), "language": plan.get("language", ""), "shift": plan["shift"],
                      "lines": [{"i": l["i"], "k": l["k"], "rhythm": l["rhythm"], "en": l["en"], "vocalise": l["vocalise"]}
                                for l in plan["lines"]]}
    elif job.kind == "check":
        d = C.track_dir(path) if Path(path).is_file() else Path(path)
        plan = json.loads((d / "plan.json").read_text(encoding="utf-8"))
        job.result = CV.check_lyrics(plan, [x if x is not None else None for x in a.get("lyrics", [])], tts)
    elif job.kind == "sing":
        lyr = a.get("lyrics")
        lyrics = None if lyr is None else [None if (x is None or str(x).strip() == "") else str(x) for x in lyr]
        if lyrics is not None:
            d = C.track_dir(path) if Path(path).is_file() else Path(path)
            (d / "lyrics_ru.txt").write_text("\n".join(f"{i:02d} | {x or ''}" for i, x in enumerate(lyrics)), encoding="utf-8")
        import ace_cover as AC
        # русский — живым вокалом ACE-Step (если стоит), в оригинале — Seed-VC по голосу певицы (там слова и так её)
        engine = str(a.get("engine") or "ace").lower()
        if not a.get("orig") and lyrics is not None and engine == "ace" and AC.available():
            job.result = AC.sing_ace(path, lyrics, progress=progress, stt_url=stt, caption=str(a.get("style") or ""))
        else:
            job.result = CV.sing(path, lyrics, progress=progress, tts_url=tts, orig=bool(a.get("orig")), steps=int(a.get("steps", 30)))
    else:
        raise ValueError(f"не знаю задачу {job.kind}")


def worker() -> None:
    while True:
        job = Q.get()
        if job.cancel:
            job.state, job.message = "cancelled", "отменено"
            continue
        job.state, job.message = "running", "начинаю…"
        touch()
        try:
            run(job)
            job.state, job.progress = "done", 1.0
            if job.message in ("начинаю…", "в очереди"):
                job.message = "готово"
        except Cancelled:
            job.state, job.message = "cancelled", "отменено"
        except Exception as e:  # задача упала — сервис живёт, клиенту — текст ошибки
            job.state, job.message = "error", f"{type(e).__name__}: {e}"
            C.log(traceback.format_exc())
        finally:
            C.free_gpu()
            touch()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send(self, code: int, obj) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8") if not isinstance(obj, bytes) else obj
        try:
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8" if not isinstance(obj, bytes) else "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (ConnectionError, BrokenPipeError):
            self.close_connection = True

    def do_GET(self):
        touch()
        if self.path == "/health":
            self._send(200, b"ok")
        elif self.path.startswith("/job/"):
            j = JOBS.get(self.path[5:])
            self._send(200 if j else 404, j.view() if j else {"error": "нет такой задачи"})
        elif self.path == "/jobs":
            self._send(200, [j.view() for j in sorted(JOBS.values(), key=lambda j: -j.created)[:20]])
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        touch()
        n = int(self.headers.get("Content-Length", "0") or 0)
        raw = self.rfile.read(n) if n > 0 else b""
        try:
            req = json.loads(raw.decode("utf-8") or "{}")
        except json.JSONDecodeError:
            self._send(400, {"error": "плохой JSON"})
            return
        if self.path == "/job":
            kind = str(req.get("kind", ""))
            if kind not in ("analyze", "plan", "check", "sing"):
                self._send(400, {"error": f"не знаю задачу {kind}"})
                return
            j = Job(kind, req)
            with LOCK:
                JOBS[j.id] = j
                # старые выполненные задачи не копятся
                for old in [k for k, v in JOBS.items() if v.state in ("done", "error", "cancelled") and time.time() - v.created > 3600]:
                    del JOBS[old]
            Q.put(j)
            self._send(200, {"id": j.id})
        elif self.path.startswith("/cancel/"):
            j = JOBS.get(self.path[8:])
            if j:
                j.cancel = True
            self._send(200 if j else 404, {"ok": bool(j)})
        else:
            self._send(404, {"error": "not found"})


def idle_watch() -> None:
    while True:
        time.sleep(30)
        busy = any(j.state in ("queued", "running") for j in JOBS.values())
        if not busy and time.time() - _last > IDLE_EXIT:
            C.log("простой 30 минут — выхожу, чтобы не держать память")
            os._exit(0)


def main() -> int:
    global STT, TTS
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8099)
    ap.add_argument("--stt", default=STT)
    ap.add_argument("--tts", default=TTS)
    a = ap.parse_args()
    STT, TTS = a.stt, a.tts
    threading.Thread(target=worker, daemon=True).start()
    threading.Thread(target=idle_watch, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", a.port), Handler)
    C.log(f"музыка слушает на 127.0.0.1:{a.port}")
    srv.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
