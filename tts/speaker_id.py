"""Голосовые профили Клавы: кто говорит — по отпечатку голоса.

Модель — microsoft/wavlm-base-plus-sv (x-vector, обучена именно на «тот же человек или нет»): на записях друзей
свой-свой ≈ 0,96, чужие 0,30–0,65. Прежний resemblyzer давал чужим 0,70–0,82 — телевизор и гости выдавались за
своих. Если WavLM не загрузился (нет модели/transformers) — запасной путь resemblyzer, как раньше.

Каждый человек — папка data/speakers/<имя>/ со ВСЕМИ его записями (до 20 последних), отпечатки считаются из них и
кэшируются в <имя>.<модель>.npy. Так при смене модели ничего не теряется: отпечатки пересчитываются из записей.
Рядом <имя>.wav — последняя запись (для прослушивания в окне профилей).

    python speaker_id.py [--port 8098] [--threshold 0.80]

    POST /enroll?name=Лис[&append=1]   body: audio/wav → добавить запись (без append — начать профиль заново)
    POST /identify                     body: audio/wav → {name, similarity, candidate, scores:{имя: близость}, threshold}
    POST /correct?wrong=А&correct=Б    body: audio/wav → убрать ближайшую запись у А, добавить эту к Б
    POST /adapt?name=Лис               body: audio/wav → осторожно доучить (только если уверенно и не похоже на других)
    POST /rename?from=А&to=Б
    GET  /list | /info | /health | /sample?name=
    DELETE /remove?name=Лис
"""
import argparse
import io
import json
import shutil
import sys
import threading
import time
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
STORE = ROOT / "data" / "speakers"
STORE.mkdir(parents=True, exist_ok=True)
WAVLM_DIR = ROOT / "models" / "speaker" / "wavlm-base-plus-sv"
MAX_SAMPLES = 20

_lock = threading.RLock()
_ready = False
_model_name = ""
_threshold = 0.80
_embed_fn = None            # функция float32[16k] -> нормированный вектор
_cache: dict[str, np.ndarray] = {}   # имя -> матрица отпечатков (N × D)


def log(m: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {m}", flush=True)


# ---------------- модель ----------------

def load_encoder(prefer_wavlm: bool = True) -> None:
    global _embed_fn, _model_name, _threshold, _ready
    t = time.time()
    if prefer_wavlm and WAVLM_DIR.exists():
        try:
            import torch
            from transformers import AutoFeatureExtractor, WavLMForXVector
            torch.set_num_threads(4)   # рядом whisper и голос — все ядра не забираем
            fe = AutoFeatureExtractor.from_pretrained(str(WAVLM_DIR))
            model = WavLMForXVector.from_pretrained(str(WAVLM_DIR)).eval()

            def emb(audio: np.ndarray) -> np.ndarray:
                x = fe(audio, sampling_rate=16000, return_tensors="pt")
                with torch.inference_mode():
                    e = model(**x).embeddings[0]
                e = torch.nn.functional.normalize(e, dim=-1)
                return e.numpy().astype(np.float32)

            _embed_fn = emb
            _model_name = "wavlm-base-plus-sv"
            log(f"WavLM x-vector загружен за {time.time() - t:.1f} с (порог «свой» {_threshold:.2f})")
            _ready = True
            return
        except Exception as e:  # модель битая или нет пакета — не остаёмся глухими, берём запасную
            log(f"WavLM не загрузился: {e!r} — беру resemblyzer")
    from resemblyzer import VoiceEncoder
    enc = VoiceEncoder()
    _embed_fn = lambda a: (lambda v: (v / (np.linalg.norm(v) + 1e-9)).astype(np.float32))(enc.embed_utterance(a))
    _model_name = "resemblyzer"
    _threshold = 0.75
    log(f"resemblyzer загружен за {time.time() - t:.1f} с (порог {_threshold:.2f})")
    _ready = True


def wav_to_mono16k(raw: bytes) -> np.ndarray:
    """WAV bytes → float32 моно 16 кГц."""
    with wave.open(io.BytesIO(raw), "rb") as f:
        n = f.getnframes(); sr = f.getframerate(); ch = f.getnchannels(); w = f.getsampwidth()
        data = f.readframes(n)
    dt = {1: np.int8, 2: np.int16, 4: np.int32}.get(w)
    if dt is None:
        raise ValueError(f"неподдерживаемая ширина сэмпла {w}")
    audio = np.frombuffer(data, dtype=dt).astype(np.float32) / float(2 ** (8 * w - 1))
    if ch > 1:
        audio = audio.reshape(-1, ch).mean(axis=1)
    if sr != 16000:
        n_out = int(len(audio) * 16000 / sr)
        audio = np.interp(np.linspace(0, len(audio) - 1, n_out), np.arange(len(audio)), audio).astype(np.float32)
    return audio


def trim_silence(a: np.ndarray) -> np.ndarray:
    """Срезать тишину по краям (у фраз от детектора голоса — секунда тишины в хвосте): отпечаток чище."""
    frame = 480  # 30 мс
    if len(a) < frame * 4:
        return a
    n = len(a) // frame
    rms = np.sqrt((a[: n * frame].reshape(n, frame) ** 2).mean(axis=1) + 1e-12)
    thr = max(rms.max() * 0.08, 0.004)
    idx = np.where(rms > thr)[0]
    if len(idx) == 0:
        return a
    s = max(0, idx[0] - 3) * frame
    e = min(n, idx[-1] + 4) * frame
    return a[s:e]


def embed_audio(audio: np.ndarray) -> np.ndarray:
    audio = trim_silence(audio)
    if len(audio) < 16000 * 0.5:
        raise ValueError("слишком коротко для отпечатка голоса (нужно ≥0,5 с речи)")
    peak = float(np.abs(audio).max())
    if peak > 1e-4:
        audio = audio * (0.9 / peak)   # громкость не должна влиять на отпечаток
    with _lock:
        return _embed_fn(audio)


def embed(raw: bytes) -> np.ndarray:
    return embed_audio(wav_to_mono16k(raw))


# ---------------- хранилище ----------------

def sanitize_name(name: str) -> str:
    """Имя файла — только буквы/цифры/подчёркивание, чтобы не выйти из STORE."""
    return "".join(c if c.isalnum() or c in "_-" else "_" for c in name.strip())[:40] or "unnamed"


def profile_dir(name: str) -> Path:
    return STORE / sanitize_name(name)


def profile_names() -> list[str]:
    names = {p.name for p in STORE.iterdir() if p.is_dir() and any(p.glob("*.wav"))}
    return sorted(names)


def migrate_old_profiles() -> None:
    """Старые профили — один <имя>.wav и <имя>.npy от resemblyzer: запись переезжает в папку профиля."""
    for w in STORE.glob("*.wav"):
        d = STORE / w.stem
        if not d.exists():
            d.mkdir(parents=True, exist_ok=True)
            shutil.copy2(w, d / f"{int(w.stat().st_mtime)}_000.wav")
            log(f"переезд: {w.stem} → папка с записями")


def samples(name: str) -> list[Path]:
    d = profile_dir(name)
    return sorted(d.glob("*.wav"), key=lambda p: p.stat().st_mtime) if d.exists() else []


WIN, HOP = 1.5, 0.75      # окна отпечатков внутри записи, секунды
MAJORITY_SIM = 0.75       # окно «того же голоса», что большинство окон профиля


def window_embeddings(audio: np.ndarray) -> list[np.ndarray]:
    """Запись режется на окна по 1,5 с: в одной записи бывает и друг, и кто-то ещё («— Почему?»)."""
    audio = trim_silence(audio)
    total = len(audio) / 16000
    if total < WIN + 0.3:
        return [embed_audio(audio)]
    out = []
    st = 0.0
    while st + WIN <= total + 1e-6:
        seg = audio[int(st * 16000):int((st + WIN) * 16000)]
        if np.sqrt((seg ** 2).mean()) > 0.003:   # тишина между словами — не отпечаток
            try:
                out.append(embed_audio(seg))
            except ValueError:
                pass
        st += HOP
    return out or [embed_audio(audio)]


def majority(vecs: np.ndarray) -> np.ndarray:
    """Оставить окна основного голоса профиля: близкие к медоиду (окну, похожему на всех остальных)."""
    if len(vecs) < 4:
        return vecs
    s = vecs @ vecs.T
    med = int(np.argmax(s.mean(axis=1)))
    keep = s[med] >= MAJORITY_SIM
    return vecs[keep] if keep.sum() >= 2 else vecs


def sample_windows(f: Path) -> np.ndarray | None:
    """Окна-отпечатки одной записи: считаются один раз и лежат рядом (<запись>.<модель>.npy).
    Раньше при каждой новой записи пересчитывался весь профиль (20 записей × окна) — /identify ждал 15 с и падал."""
    cf = f.with_suffix(f".{_model_name}.npy")
    if cf.exists() and cf.stat().st_mtime >= f.stat().st_mtime:
        try:
            m = np.load(cf)
            if m.ndim == 2 and len(m) > 0:
                return m
        except (OSError, ValueError):
            pass
    try:
        m = np.vstack(window_embeddings(wav_to_mono16k(f.read_bytes())))
    except Exception as e:  # битая или слишком короткая запись — пропускаем её, не весь профиль
        log(f"запись {f.parent.name}/{f.name} пропущена ({e})")
        return None
    np.save(cf, m)
    return m


def profile_matrix(name: str) -> np.ndarray | None:
    """Отпечатки основного голоса человека по всем его записям (окна каждой записи — из кэша)."""
    with _lock:
        if name in _cache:
            return _cache[name]
        mats = [m for f in samples(name) if (m := sample_windows(f)) is not None]
        if not mats:
            return None
        allv = np.vstack(mats)
        m = majority(allv)
        if len(m) < len(allv):
            log(f"{name}: из {len(allv)} окон оставлено {len(m)} — остальное чужие голоса/шум")
        _cache[name] = m
        return m


def invalidate(name: str) -> None:
    """Сбросить собранный профиль в памяти; окна отдельных записей остаются в своих кэшах."""
    with _lock:
        _cache.pop(name, None)


def drop_sample(f: Path) -> None:
    """Удалить запись профиля вместе с кэшем её окон."""
    for p in (f, f.with_suffix(f".{_model_name}.npy")):
        if p.exists():
            p.unlink()


def score(emb: np.ndarray, m: np.ndarray) -> float:
    """Близость к человеку: среднее лучших совпадений с окнами его голоса — одно кривое окно не решает."""
    sims = np.sort(m @ emb)[::-1]
    k = min(5, max(1, len(sims) // 3))
    return float(sims[:k].mean())


def add_sample(name: str, raw: bytes, reset: bool = False) -> int:
    d = profile_dir(name)
    with _lock:
        if reset and d.exists():
            shutil.rmtree(d)
        d.mkdir(parents=True, exist_ok=True)
        (d / f"{int(time.time() * 1000)}.wav").write_bytes(raw)
        files = samples(name)
        for old in files[:-MAX_SAMPLES]:
            drop_sample(old)
        (STORE / f"{sanitize_name(name)}.wav").write_bytes(raw)
        invalidate(name)
        return len(samples(name))


# ---------------- операции ----------------

def enroll(name: str, raw: bytes, append: bool = False) -> dict:
    embed(raw)   # проверка, что запись годится (длина, формат) — до записи на диск
    n = add_sample(name, raw, reset=not append)
    profile_matrix(name)
    log(f"enroll{'+' if append else ''}: {name} (записей: {n}, {len(raw) / 1024:.1f} КБ)")
    return {"name": name, "samples": n}


def identify(raw: bytes) -> dict:
    emb = embed(raw)
    scores: dict[str, float] = {}
    for name in profile_names():
        m = profile_matrix(name)
        if m is not None:
            scores[name] = round(score(emb, m), 4)
    if not scores:
        return {"name": None, "similarity": 0.0, "candidate": None, "scores": {}, "threshold": _threshold}
    best = max(scores, key=scores.get)
    sim = scores[best]
    out = {"similarity": sim, "scores": scores, "threshold": _threshold}
    if sim < _threshold:
        out.update(name=None, candidate=best)
    else:
        out.update(name=best, candidate=best)
    return out


def penalize_and_correct(wrong: str, correct: str, raw: bytes) -> dict:
    """Модель ошиблась: у wrong убираем запись, ближайшую к этой фразе (она и дала ложное совпадение), фразу — к correct."""
    emb = embed(raw)
    removed = None
    if wrong and wrong != correct:
        files = samples(wrong)
        if len(files) > 1:
            best_i, best_s = -1, -1.0
            for i, f in enumerate(files):
                try:
                    s = max(float(v @ emb) for v in window_embeddings(wav_to_mono16k(f.read_bytes())))
                except Exception:
                    continue
                if s > best_s:
                    best_i, best_s = i, s
            if best_i >= 0:
                removed = best_s
                drop_sample(files[best_i])
                invalidate(wrong)
                log(f"штраф: у {wrong} убрана запись {files[best_i].name} (близость {removed:.2f})")
    n = add_sample(correct, raw)
    log(f"штраф: фраза ушла в {correct} (записей {n})")
    return {"ok": True, "penalized": wrong, "removed_similarity": removed, "corrected": correct}


def adaptive_update(name: str, raw: bytes) -> dict:
    """Доучивание на лету: только если фраза уверенно его и заметно дальше от всех остальных."""
    if not name or name == "Клава":
        return {"ok": False, "reason": "не доучиваем"}
    m = profile_matrix(name)
    if m is None:
        return {"ok": False, "reason": "профиль не найден"}
    emb = embed(raw)
    own = score(emb, m)
    other = max((score(emb, mm) for n2 in profile_names() if n2 != name and (mm := profile_matrix(n2)) is not None), default=-1.0)
    if own < _threshold + 0.08 or own - other < 0.15:
        return {"ok": False, "reason": f"не уверена (свой {own:.2f}, чужой {other:.2f})"}
    n = add_sample(name, raw)
    log(f"adapt: {name} доучен (свой {own:.2f}, ближайший чужой {other:.2f}, записей {n})")
    return {"ok": True, "name": name, "samples": n, "similarity": own, "margin": own - other}


def list_profiles() -> list[dict]:
    out = []
    for name in profile_names():
        files = samples(name)
        out.append({"name": name, "updated": max(f.stat().st_mtime for f in files), "samples": len(files)})
    return out


def remove_profile(name: str) -> dict:
    removed = False
    d = profile_dir(name)
    with _lock:
        if d.exists():
            shutil.rmtree(d); removed = True
        for p in STORE.glob(f"{sanitize_name(name)}.*"):
            p.unlink(); removed = True
        _cache.pop(name, None)
    return {"removed": name} if removed else {"removed": None, "reason": "не найден"}


def rename_profile(old: str, new: str) -> dict:
    o, n = sanitize_name(old), sanitize_name(new)
    if o == n:
        return {"renamed": None, "reason": "имя не изменилось"}
    with _lock:
        if (STORE / o).exists():
            if (STORE / n).exists():
                shutil.rmtree(STORE / n)
            (STORE / o).rename(STORE / n)
        for p in list(STORE.glob(f"{o}.*")):
            p.rename(STORE / (n + p.name[len(o):]))
        _cache.pop(old, None)
    log(f"rename: {old} → {new}")
    return {"renamed": new}


def get_sample(name: str) -> bytes | None:
    p = STORE / f"{sanitize_name(name)}.wav"
    if p.exists():
        return p.read_bytes()
    files = samples(name)
    return files[-1].read_bytes() if files else None


# ---------------- HTTP ----------------

class H(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send(self, code: int, body: bytes, ctype: str = "application/json"):
        try:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (ConnectionError, BrokenPipeError):
            pass

    def _json(self, code: int, obj):
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

    def _read_body(self) -> bytes:
        n = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(n) if n > 0 else b""

    def _qs(self) -> dict:
        return {k: v[0] for k, v in parse_qs(urlparse(self.path).query).items()}

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/health":
            self._send(200 if _ready else 503, b"ok" if _ready else b"loading", "text/plain")
        elif path == "/info":
            self._json(200, {"model": _model_name, "threshold": _threshold, "profiles": len(profile_names()), "ready": _ready})
        elif path == "/list":
            self._json(200, list_profiles())
        elif path == "/sample":
            wav = get_sample(self._qs().get("name", ""))
            if wav:
                self._send(200, wav, "audio/wav")
            else:
                self._send(404, b"no sample", "text/plain")
        else:
            self._send(404, b"not found", "text/plain")

    def do_DELETE(self):
        if urlparse(self.path).path == "/remove":
            name = self._qs().get("name", "")
            if not name:
                self._json(400, {"error": "нет ?name="}); return
            self._json(200, remove_profile(name))
        else:
            self._send(404, b"not found", "text/plain")

    def do_POST(self):
        if not _ready:
            self._send(503, b"loading", "text/plain"); return
        path = urlparse(self.path).path
        raw = self._read_body()
        q = self._qs()
        try:
            if path == "/enroll":
                name = q.get("name", "")
                if not name:
                    self._json(400, {"error": "нет ?name="}); return
                self._json(200, enroll(name, raw, append=q.get("append", "").lower() in ("1", "true", "yes")))
            elif path == "/identify":
                self._json(200, identify(raw))
            elif path == "/rename":
                if not q.get("from") or not q.get("to"):
                    self._json(400, {"error": "нужно ?from=&to="}); return
                self._json(200, rename_profile(q["from"], q["to"]))
            elif path in ("/correct", "/penalize"):
                if not q.get("correct"):
                    self._json(400, {"error": "нужно ?correct="}); return
                self._json(200, penalize_and_correct(q.get("wrong", ""), q["correct"], raw))
            elif path == "/adapt":
                if not q.get("name"):
                    self._json(400, {"error": "нужно ?name="}); return
                self._json(200, adaptive_update(q["name"], raw))
            else:
                self._send(404, b"not found", "text/plain")
        except ValueError as e:   # слишком коротко / не тот формат — это не сбой сервера
            self._json(400, {"error": str(e)})
        except Exception as e:
            log(f"error {path}: {e!r}")
            self._json(500, {"error": str(e)})


class Server(ThreadingHTTPServer):
    daemon_threads = True


def main() -> None:
    global _threshold
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8098)
    ap.add_argument("--threshold", type=float, default=0.80, help="близость «тот же человек» (0..1); для resemblyzer берётся 0,75")
    ap.add_argument("--resemblyzer", action="store_true", help="старая модель вместо WavLM")
    ap.add_argument("--store", help="папка профилей (для проверки на копии); по умолчанию data/speakers")
    args = ap.parse_args()
    _threshold = args.threshold
    if args.store:
        global STORE
        STORE = Path(args.store)
        STORE.mkdir(parents=True, exist_ok=True)
    migrate_old_profiles()
    load_encoder(prefer_wavlm=not args.resemblyzer)
    t = time.time()
    for name in profile_names():   # отпечатки всех записей — сразу, чтобы первая фраза не ждала пересчёта
        m = profile_matrix(name)
        log(f"профиль {name}: записей {len(samples(name))}, окон отпечатка {0 if m is None else len(m)}")
    log(f"отпечатки готовы за {time.time() - t:.1f} с")
    srv = Server(("127.0.0.1", args.port), H)
    log(f"speaker_id слушает http://127.0.0.1:{args.port} (модель {_model_name}, порог {_threshold})")
    srv.serve_forever()


if __name__ == "__main__":
    sys.exit(main())
