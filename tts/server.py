"""Сервер голоса Клавы: Qwen3-TTS клонирует тембр по эталону tts/voice.json, ударения ставит по знакам.

Модель — Qwen3-TTS-12Hz-1.7B-Base с влитым адаптером ударений (models/Qwen3-TTS-12Hz-1.7B-Base-ru-stress,
собирается tts/merge_stress_lora.py): она понимает знак U+0301 после ударной гласной. Знаки расставляет
ru_text.py (RUAccent + свой словарь tts/custom_stress.json). Нет слитой модели — старая 0.6B без знаков.

Движок — faster-qwen3-tts (MIT): статический KV-кэш + torch.cuda.CUDAGraph. Обычный qwen-tts на этой машине
давал RTF ~7: каждая из тысяч мелких операций на кадр упиралась в запуск ядра CUDA под WDDM.

    POST /tts     {"text": "...", "emotion": "angry"}  -> audio/wav (PCM 16 бит, моно)
    POST /tts_stream  то же -> сырой PCM 16 бит моно кусками по мере синтеза (Transfer-Encoding: chunked,
                  частота в заголовке X-Sample-Rate): первый звук через ~0,5 с, а не после всей фразы (друг 29.09:
                  «убрать задержку перед ответом»). Обученные эмоции (LoRA, смех по метке) идут целиком, как раньше.
    POST /reload                   -> перечитать tts/custom_stress.json и tts/emotions.json без перезапуска
    GET  /emotions                 -> какие эмоции голоса есть и как устроены

Эмоции (tts/emotions.json, 28.09): клон копирует не только тембр, но и манеру эталона — с одним ласковым klava_02 злые
фразы звучали так же ласково. На эмоцию — свой эталон того же тембра (ref: собран VoiceDesign и проверен по отпечатку
голоса, tts/emotion_voice.py) или klava_02 с указанием манеры (instruct). Громкость речи выравнивается под эмоцию:
злость громче, шёпот тише. Обученные эмоции (xvec) говорятся с эмоциональной LoRA models/emo_lora — она включается
в веса на лету только на эти фразы (EmoSwitch), спокойная речь остаётся исходной моделью.
    POST /accent  {"text": "..."}  -> {"text": "..."} ударения «+» перед ударной гласной (тренажёр чтения)
    GET  /health                   -> 200, когда модель загружена и прогрета

Видеокарта одна: запросы выполняются по одному под замком. HTTP/1.1 с keep-alive: соединение живёт между
фразами (на HTTP/1.0 каждое новое соединение изредка рвалось, не дождавшись ответа).

    python server.py [--port 8095] [--model <папка>] [--bench]
"""
import argparse
import difflib
import io
import json
import os
import re
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from faster_qwen3_tts import FasterQwen3TTS

import ru_text

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "models"
STRESS_MODEL = MODELS / "Qwen3-TTS-12Hz-1.7B-Base-ru-stress"
# эмоциональная LoRA (tts/emo_train: RESD + шёпот; учили «отпечаток + манера», непотоково). Не вливается навсегда:
# слитая модель (emo_train/merge.py) и спокойную речь поднимала с ~284 до ~332 Гц (проба 28.09) — а спокойный голос
# друг уже принял. Поэтому адаптер включается на лету только для обученных эмоций (xvec в emotions.json), см. EmoSwitch
EMO_LORA = MODELS / "emo_lora"
PLAIN_MODEL = MODELS / "Qwen3-TTS-12Hz-0.6B-Base"
VOICE = ROOT / "tts" / "voice.json"
EMOTIONS = ROOT / "tts" / "emotions.json"

_lock = threading.Lock()
_model = None
_marks = False
_ref = ""
_ref_text = ""
_ready = False
_emotions: dict[str, dict] = {}   # эмоция -> {ref, ref_text (готовый для модели) | instruct, loud_db, xvec}
_emo_model = False                # эмоциональная LoRA есть: влита в модель (--model …-emo) или включается на лету
_emo = None                       # EmoSwitch — адаптер на лету; None — нет адаптера или он влит навсегда


def log(msg: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


class EmoSwitch:
    """
    Эмоциональная LoRA поверх загруженной модели — точной заплаткой битов. Включённые веса те же, что раньше:
    on = bf16(W + B·A·alpha/r) (ровно как emo_train/merge.py); но хранится не копия исходника, а разница битовых
    образов d = биты(on) − биты(W) по модулю 2^16. Включить — к int16-виду весов прибавить d, выключить — вычесть: целая
    арифметика с переполнением, обратима байт в байт и без накопления ошибки округления (дробное вычитание дельты копило
    бы её с каждым переключением).
    Раньше держалась полная копия исходника в закреплённой памяти — 2838 МиБ ОЗУ (замер 18.11). У d 98,7 % значений
    в int8 (|d| ≤ 127), остальные 1,3 % (переходы через ноль) — исключениями (номер int32 + значение int16): ~1,5 ГиБ.
    CUDA-графы faster-qwen3-tts читают веса по тем же адресам, поэтому перезаписи на месте хватает.
    """

    def __init__(self, talker: torch.nn.Module, lora_dir: Path):
        from safetensors.torch import load_file
        cfg = json.loads((lora_dir / "adapter.json").read_text(encoding="utf-8"))
        scale = cfg["alpha"] / cfg["rank"]
        pairs: dict[str, dict[str, torch.Tensor]] = {}
        for k, v in load_file(str(lora_dir / "adapter.safetensors")).items():
            stem, part = k.rsplit(".lora_", 1)
            pairs.setdefault(stem, {})[part.split(".")[0]] = v
        params = dict(talker.named_parameters())
        # (вес, d в int8, номера исключений int32, значения исключений int16) — заплатки в закреплённой памяти
        self.items: list[tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]] = []
        parts: list[tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]] = []   # пока на видеокарте
        with torch.no_grad():
            for stem, ab in pairs.items():
                p = params.get(f"{stem}.weight")
                if p is None or "A" not in ab or "B" not in ab:
                    raise ValueError(f"адаптер не от этой модели: нет {stem}.weight")
                w = p.data
                b, a = ab["B"].to(w.device, torch.float32), ab["A"].to(w.device, torch.float32)
                if (b.shape[0], a.shape[1]) != tuple(w.shape):
                    raise ValueError(f"{stem}: форма {(b.shape[0], a.shape[1])} против {tuple(w.shape)}")
                on = (w.float() + (b @ a) * scale).to(w.dtype)
                del b, a
                d = on.view(torch.int16).to(torch.int32) - w.view(torch.int16).to(torch.int32)
                d = torch.remainder(d + 32768, 65536) - 32768
                big = (d < -128) | (d > 127)
                idx = big.flatten().nonzero().squeeze(1).to(torch.int32)
                val = d.flatten()[idx.long()].to(torch.int16)
                d8 = torch.where(big, torch.zeros_like(d), d).to(torch.int8)
                # проверка: из заплатки собирается ровно on — иначе адаптер не включаем вовсе
                dd = d8.to(torch.int16)
                dd.view(-1)[idx.long()] = val
                if not torch.equal(w.view(torch.int16) + dd, on.view(torch.int16)):
                    raise ValueError(f"{stem}: заплатка не сходится с включёнными весами")
                del on, d, big, dd
                parts.append((w, d8, idx, val))
            # Заплатки — в закреплённые блоки по 256 МиБ (последний — степень двойки под остаток). Аллокатор закреплённой
            # памяти PyTorch округляет КАЖДЫЙ блок до степени двойки: 693 мелких блока давали +550 МиБ сверху, а память
            # через cudaHostRegister он закреплённой не считает — переключение шло 700 мс вместо 240 (замеры 18.11)
            def aligned(n: int) -> int:
                return (n + 255) & ~255
            sizes = [aligned(t.numel() * t.element_size()) for _, *ts in parts for t in ts]
            total = sum(sizes)
            block = 256 * 2**20
            self._bufs: list[torch.Tensor] = []
            buf, off, left = None, 0, total

            def new_buf(need: int) -> torch.Tensor:
                n = block if left >= block else 1 << max(16, (max(left, need) - 1).bit_length())
                b = torch.empty(max(n, need), dtype=torch.uint8, pin_memory=True)
                self._bufs.append(b)
                return b

            for w, *ts in parts:
                views = []
                for t in ts:
                    n = t.numel() * t.element_size()
                    if buf is None or off + n > buf.numel():
                        buf, off = new_buf(n), 0
                    v = buf[off:off + n].view(t.dtype).view(t.shape)
                    v.copy_(t)
                    views.append(v)
                    off += aligned(n)
                    left -= aligned(n)
                self.items.append((w, views[0], views[1], views[2]))
            del parts
        torch.cuda.empty_cache()
        self.on = False
        self.mib = sum(b.numel() for b in self._bufs) / 2**20

    @torch.no_grad()
    def set(self, on: bool) -> float:
        """Включить/выключить адаптер; вернёт, сколько секунд заняло (0 — уже в нужном состоянии)."""
        if on == self.on:
            return 0.0
        t = time.time()
        for w, d8, idx, val in self.items:
            dd = d8.to(w.device, non_blocking=True).to(torch.int16)
            if idx.numel():
                dd.view(-1)[idx.to(w.device, non_blocking=True).long()] = val.to(w.device, non_blocking=True)
            wi = w.view(torch.int16)
            if on:
                wi.add_(dd)
            else:
                wi.sub_(dd)
        torch.cuda.synchronize()
        self.on = on
        return time.time() - t


def load(model_dir: Path) -> None:
    global _model, _marks, _ref, _ref_text, _ready, _emo_model, _emo
    _marks = (model_dir / "merge.json").exists()   # слитая с адаптером ударений — понимает знаки
    _emo_model = "emotion_adapter" in (json.loads((model_dir / "merge.json").read_text(encoding="utf-8")) if _marks else {})
    voice = json.loads(VOICE.read_text(encoding="utf-8"))
    ref = Path(voice["ref"])
    if not ref.is_absolute():
        ref = ROOT / ref
    _ref = str(ref)
    # RUAccent грузится ~15 с на процессоре — параллельно с моделью на видеокарте. Сами модули (он и transformers)
    # импортируются здесь, в главном потоке: одновременный импорт transformers из двух потоков отдаёт недостроенный модуль
    import ruaccent  # noqa: F401
    import transformers  # noqa: F401
    acc_err: list[BaseException] = []

    def load_accent():
        try:
            t0 = time.time()
            ru_text.load()
            log(f"ударения: RUAccent и свой словарь загружены за {time.time() - t0:.1f} с")
        except BaseException as e:   # без ударений голос всё равно работает — хуже, но не немой
            acc_err.append(e)

    acc = threading.Thread(target=load_accent, daemon=True)
    acc.start()
    t = time.time()
    _model = FasterQwen3TTS.from_pretrained(
        # 1024 позиций статического кэша хватает на реплику ~40 с (эталон ~10 с + текст + 12 кадров/с)
        str(model_dir), device="cuda", dtype=torch.bfloat16, attn_implementation="sdpa", max_seq_len=1024)
    log(f"модель {model_dir.name} загружена за {time.time() - t:.1f} с, VRAM {torch.cuda.memory_allocated() / 2**30:.2f} ГиБ, "
        f"эталон {ref.name}, знаки ударения {'да' if _marks else 'нет'}")
    if not _emo_model and (EMO_LORA / "adapter.safetensors").exists():
        base = json.loads((EMO_LORA / "adapter.json").read_text(encoding="utf-8")).get("base")
        if base != model_dir.name:
            log(f"эмоции: адаптер {EMO_LORA.name} учился на {base}, а загружена {model_dir.name} — без него")
        else:
            t = time.time()
            try:
                _emo = EmoSwitch(_model.model.model.talker, EMO_LORA)
                _emo_model = True
                log(f"эмоции: адаптер {EMO_LORA.name} готов включаться на лету ({len(_emo.items)} матриц, точная заплатка "
                    f"{_emo.mib:.0f} МиБ в памяти) за {time.time() - t:.1f} с")
            except (ValueError, RuntimeError) as e:
                log(f"эмоции: адаптер не подошёл ({e}) — говорю без обученных эмоций")
    acc.join()
    if acc_err:
        log(f"ударения: RUAccent не загрузился ({acc_err[0]!r}) — говорю без знаков")
    _ref_text = prepare(voice["text"])[0]
    load_emotions()
    t = time.time()
    synth("Мур.")  # первый вызов записывает CUDA-графы и кэширует эталон голоса
    synth("Привет, я тут, мур.")   # и усиление спокойного голоса для потока (громкость целой фразы)
    synth_stream("Ну что, поехали.", "", lambda w, s: None)   # потоковый путь — тоже прогреть
    log(f"прогрев (запись графов) {time.time() - t:.1f} с")
    _ready = True


def load_emotions() -> int:
    """tts/emotions.json → _emotions. Битые записи (нет файла эталона) пропускаются с записью в журнал."""
    global _emotions
    if not EMOTIONS.exists():
        _emotions = {}
        return 0
    raw = json.loads(EMOTIONS.read_text(encoding="utf-8"))
    res: dict[str, dict] = {}
    for name, e in raw.items():
        if name.startswith("_") or name == "nonverbal" or not isinstance(e, dict):   # nonverbal — банки звуков, не стиль
            continue
        item: dict = {"loud_db": float(e["loud_db"]) if "loud_db" in e else None}
        if "ref" in e:
            ref = Path(e["ref"])
            ref = ref if ref.is_absolute() else ROOT / ref
            if not ref.exists():
                log(f"эмоция {name}: нет эталона {ref} — пропускаю")
                continue
            item["ref"] = str(ref)
            item["ref_text"] = prepare(str(e.get("text", "")))[0]
        elif "instruct" in e:
            item["instruct"] = str(e["instruct"])
        # xvec: отпечаток klava_02 без её манеры + указание, непотоково — так эмоцию учили (emo_train); только если
        # эмоциональная модель загружена, иначе обычный клон с указанием
        item["xvec"] = bool(e.get("xvec")) and _emo_model
        # lora: обученный адаптер, но клон С ЭТАЛОНОМ (klava_02 + манера) — полный тембр без «робота» режима «только отпечаток»
        item["lora"] = bool(e.get("lora")) and _emo is not None
        # laugh_marker: адаптер выучил смех внутри речи по метке 🤣 (emo_train/prepare_nvtts.py) — «ха-ха» в тексте
        # становится меткой, а без «ха-ха» метка с вероятностью marker_chance ставится в начало фразы
        if "max_f0" in e:
            item["max_f0"] = float(e["max_f0"])
        if "min_f0" in e:
            item["min_f0"] = float(e["min_f0"])
        if e.get("laugh_marker") and item["lora"]:
            item["laugh_marker"] = True
            item["marker_chance"] = float(e.get("marker_chance", 0.5))
        # bank: живой звук из банка (смех, плач, вздох, стон) — с вероятностью bank_chance перед фразой
        if e.get("bank"):
            item["bank"] = str(e["bank"])
            item["bank_chance"] = float(e.get("bank_chance", 0.5))
        # interjections: её же голосом вставка перед фразой (ахегао — комичное анимешное «Ах, ямэро-о!»), с вероятностью
        # interjection_chance
        if isinstance(e.get("interjections"), list) and e["interjections"]:
            item["interjections"] = [str(x) for x in e["interjections"] if str(x).strip()]
            item["interjection_chance"] = float(e.get("interjection_chance", 0.5))
        res[name] = item
    _emotions = res
    kinds = [k + ("(эталон)" if "ref" in v else "(обучена)" if v.get("xvec") else "(обучена+эталон)" if v.get("lora")
                  else "(манера)" if "instruct" in v else "") for k, v in res.items()]
    nv = dict(raw.get("nonverbal") or {})
    if raw.get("laughs"):                       # прежний формат: только смешки из tts/voices/laughs
        nv.setdefault("laugh", [f"../laughs/{n}" for n in raw["laughs"]])
    counts = load_banks(nv)
    log("эмоции голоса: " + ", ".join(kinds) + "; живые звуки: " + (", ".join(f"{k} {v}" for k, v in counts.items() if v) or "нет"))
    return len(res)


HISS_RX = re.compile(r"^\W*(т+с+|п+ф+|ш+|ф+|пс+|ц+)[-!.,…\s]", re.IGNORECASE)

MAX_F0 = 350.0   # Гц: выше — уже «лоляшный» тон (обычный голос Клавы ~300, klava_02 316)
# Гц: ниже — уже не её голос, а «грубый мужской» (друг 10.10: «пиздец как страшно, когда она так тональность меняет»).
# 10.10 в журнале синтеза дубли [sly]/[annoyed] с адаптером ложились на 218–238 Гц; мужские голоса ~100–160.
# У стиля может быть свой порог min_f0 в emotions.json
MIN_F0 = 235.0

# «ха-ха», «хи-хи», «ахаха», «хе-хе» — смех текстом: модель читает его слогами («как будто текстом», друг 28.09)
LAUGH_RX = re.compile(r"(?<![а-яё])(?:а?(?:ха|хи|хе)(?:[-\s]?(?:ха|хи|хе|х))+[а-яё]*|ахах[а-яё]*|хах[а-яё]*)[!.…]*",
                      re.IGNORECASE)
NONVERBAL = ROOT / "tts" / "voices" / "nonverbal"
# живые звуки её тембром (tts/nonverbal/convert.py: ASVP-ESD → Seed-VC, выбраны другом): смех, плач, вздох, стон…
_banks: dict[str, list[tuple[np.ndarray, int]]] = {}


def load_banks(nv: dict) -> dict[str, int]:
    """Банки из emotions.json «nonverbal»: {"laugh": ["laugh_03.wav", …], "cry": […]} — файлы из tts/voices/nonverbal."""
    global _banks
    banks: dict[str, list[tuple[np.ndarray, int]]] = {}
    for kind, names in nv.items():
        bank = []
        for n in names or []:
            p = (NONVERBAL / n).resolve()
            if p.exists():
                w, sr = sf.read(p, dtype="float32")
                bank.append((w if w.ndim == 1 else w.mean(axis=1), sr))
            else:
                log(f"живой звук {kind}: нет файла {p}")
        banks[kind] = bank
    _banks = banks
    return {k: len(v) for k, v in banks.items()}


def bank_clip(kind: str, sr: int) -> np.ndarray | None:
    bank = _banks.get(kind) or []
    if not bank:
        return None
    w, s = bank[np.random.randint(len(bank))]
    if s != sr:
        import librosa
        w = librosa.resample(w, orig_sr=s, target_sr=sr)
    w = tidy(w.astype(np.float32), sr)
    # перед фразой живой звук — не дольше 2,6 с (длинный смех задерживал бы саму реплику), конец — плавно на нет
    cap = int(2.6 * sr)
    if len(w) > cap:
        w = w[:cap].copy()
        fade = int(0.25 * sr)
        w[-fade:] *= np.linspace(1.0, 0.0, fade, dtype=np.float32)
    return w


def f0_frames(wav: np.ndarray, sr: int) -> list[float]:
    """Основной тон звучащих кадров 40 мс (автокорреляция 75..600 Гц). Берётся ПЕРВЫЙ пик не ниже 85 % наибольшего:
    у живого голоса пики на периоде T и на 2T почти равны, и «самый высокий» иногда выпадал на 2T — тон вдвое ниже
    (300 Гц → 150). Пока ловили только высокий тон, это было не страшно; теперь по низкому ловим «мужской голос»."""
    frame = int(sr * 0.04)
    lo, hi = int(sr / 600), int(sr / 75)
    nfft = 1 << (2 * frame - 1).bit_length()
    f0s = []
    for i in range(0, len(wav) - frame, frame):
        x = wav[i:i + frame].astype(np.float64)
        x -= x.mean()
        if np.sqrt(np.mean(x * x)) < 0.02:
            continue
        # автокорреляция через FFT — тот же результат, что np.correlate, но кадр за десятки микросекунд: тон меряется
        # на каждом куске потока, пока он не ушёл в колонки
        spec = np.fft.rfft(x, nfft)
        ac = np.fft.irfft(spec * np.conj(spec), nfft)[:frame]
        if ac[0] <= 0:
            continue
        seg = ac[lo:hi]
        best = int(np.argmax(seg))
        if seg[best] / ac[0] <= 0.3:
            continue
        thr = 0.85 * seg[best]
        for j in range(1, best):
            if seg[j] >= thr and seg[j] >= seg[j - 1] and seg[j] >= seg[j + 1]:
                best = j
                break
        f0s.append(sr / (lo + best))
    return f0s


def median_f0(wav: np.ndarray, sr: int) -> float:
    """Медиана основного тона по звучащим кадрам (f0_frames); 0 — тона нет (шёпот, тишина)."""
    f0s = f0_frames(wav, sr)
    return float(np.median(f0s)) if len(f0s) >= 5 else 0.0


MAX_GAP = 1.2   # с: пауза длиннее — модель застряла в тишине (живые паузы на «…» — до ~0,9 с)
GAP_KEEP = 0.5  # с: во что сжимается длинная пауза внутри фразы


def quiet_frames(wav: np.ndarray, sr: int, step: float = 0.05) -> np.ndarray:
    """Тихие кадры по 50 мс: на 36 дБ тише громких (95-й перцентиль) — шёпот тише речи, поэтому порог относительный."""
    fr = int(sr * step)
    n = len(wav) // fr
    if n == 0:
        return np.zeros(0, dtype=bool)
    rms = np.sqrt(np.mean(wav[:n * fr].reshape(n, fr) ** 2, axis=1))
    return rms < max(1e-4, float(np.percentile(rms, 95)) * 0.015)


def runs(mask: np.ndarray) -> list[tuple[int, int]]:
    """Отрезки подряд идущих True: [(начало, конец)) в кадрах."""
    out, start = [], None
    for i, q in enumerate(mask):
        if q and start is None:
            start = i
        elif not q and start is not None:
            out.append((start, i))
            start = None
    if start is not None:
        out.append((start, len(mask)))
    return out


class Take:
    """Один вариант синтеза и его беды. Тяжёлые — речь сломана (не остановилась, мало звука на такой текст), лёгкая —
    тон выше MAX_F0. Лучший вариант — меньше тяжёлых, потом меньше бед, потом ниже тон. Долгая тишина при целой речи
    не беда: её подрезает tidy (иначе вариант с паузой 1,2 с проигрывал «лольке» на 414 Гц)."""

    def __init__(self, wav: np.ndarray, sr: int, chars: int, cap_s: float, max_f0: float = MAX_F0,
                 min_f0: float = MIN_F0):
        self.wav, self.sr = wav, sr
        self.f0 = median_f0(wav, sr)
        self.bad: list[str] = []
        self.severe = 0
        if self.f0 > max_f0:
            self.bad.append(f"тон {self.f0:.0f} Гц выше {max_f0:.0f}")
        if 0 < self.f0 < min_f0:
            # не её голос («грубый мужской») — тяжёлая беда: без неё «ниже тон» в key() даже предпочитал такой дубль
            self.bad.append(f"тон {self.f0:.0f} Гц ниже {min_f0:.0f} — не её голос")
            self.severe += 1
        q = quiet_frames(wav, sr)
        fr = int(sr * 0.05)
        n = len(wav) // fr
        loud = np.sqrt(np.mean(wav[:n * fr].reshape(n, fr) ** 2, axis=1)) > 0.003 if n else np.zeros(0, dtype=bool)
        sound = float(np.sum(~q & loud)) * 0.05
        for cond, what in ((len(wav) / sr >= cap_s - 0.3, f"не остановилась за {cap_s:.1f} с"),
                           (chars >= 8 and sound < chars / 30, f"звука {sound:.1f} с на {chars} знаков")):
            if cond:
                self.bad.append(what)
                self.severe += 1

    def key(self) -> tuple[int, int, float]:
        # при равных бедах — ближе к её обычному голосу (~300 Гц), а не «ниже тон» (так выигрывали низкие дубли)
        return self.severe, len(self.bad), abs(self.f0 - 300.0) if self.f0 else 999.0

    def why(self) -> str:
        return ", ".join(self.bad) or "чисто"


def tidy(wav: np.ndarray, sr: int) -> np.ndarray:
    """Тишину в начале — до 60 мс, в конце — до 200 мс, паузы внутри длиннее MAX_GAP — до GAP_KEEP."""
    step = 0.05
    fr = int(sr * step)
    q = quiet_frames(wav, sr, step)
    if len(q) == 0 or q.all():
        return wav
    head, tail, cuts = 0, len(wav), []   # cuts — выбрасываемые куски [от, до) в отсчётах
    for a, b in runs(q):
        if a == 0:
            head = max(0, b * fr - int(0.06 * sr))
        elif b == len(q):
            tail = min(len(wav), a * fr + int(0.2 * sr))
        elif (b - a) * step > MAX_GAP:
            cuts.append((a * fr + int(GAP_KEEP * sr), b * fr))
    parts, pos = [], head
    for s, e in cuts:
        parts.append(wav[pos:s])
        pos = e
    parts.append(wav[pos:tail])
    return np.concatenate(parts)


def level_gain(wav: np.ndarray, sr: int, loud_db: float | None) -> float:
    """Во сколько раз усилить, чтобы громкость речи (средний RMS звучащих кадров 40 мс) стала loud_db; пик ≤ 0,97."""
    if loud_db is None or len(wav) == 0:
        return 1.0
    frame = int(sr * 0.04)
    n = len(wav) // frame
    if n == 0:
        return 1.0
    rms = np.sqrt(np.mean(wav[:n * frame].reshape(n, frame) ** 2, axis=1))
    voiced = rms[rms > 0.01]
    if len(voiced) == 0:
        return 1.0
    gain = 10 ** ((loud_db - 20 * np.log10(float(np.mean(voiced)))) / 20)
    peak = float(np.max(np.abs(wav))) * gain
    if peak > 0.97:
        gain *= 0.97 / peak
    return float(gain)


# усиление по стилям из прошлых фраз — для потока: громкость целой фразы заранее не известна
_gain_est: dict[str, float] = {}


def level(wav: np.ndarray, sr: int, loud_db: float | None, style: str = "") -> np.ndarray:
    """Громкость речи → loud_db (см. level_gain); запоминает усиление стиля для потокового синтеза."""
    gain = level_gain(wav, sr, loud_db)
    if style and loud_db is not None and gain != 1.0:
        _gain_est[style] = gain if style not in _gain_est else 0.7 * _gain_est[style] + 0.3 * gain
    return (wav * gain).astype(np.float32) if gain != 1.0 else wav


LAUGH_MARK = "🤣"


def prepare(text: str) -> tuple[str, str]:
    """(что уходит в модель, что писать в журнал). Метку смеха 🤣 ударения выкидывают — куски между метками готовятся
    по отдельности и склеиваются обратно с меткой."""
    if LAUGH_MARK in text:
        pieces = [prepare(x) if x.strip() else ("", "") for x in text.split(LAUGH_MARK)]
        return (f" {LAUGH_MARK} ".join(a for a, _ in pieces).strip(), f" {LAUGH_MARK} ".join(b for _, b in pieces).strip())
    to_model, marked = ru_text.prepare(text)
    if not _marks:
        to_model = to_model.replace(ru_text.ACUTE, "")
    return to_model, marked


def synth(text: str, emotion: str = "") -> tuple[np.ndarray, int, float]:
    """
    Фраза голосом эмоции. Живые звуки из банков вместо чтения слогами: «ха-ха» в тексте — смешок; у эмоции с банком
    ([смех], [плач], [вздох], [стон]) — с вероятностью bank_chance её звук перед фразой, как у живого человека.
    """
    style = _emotions.get(emotion) or {}
    kind = style.get("bank")
    if style.get("interjections") and np.random.rand() < style.get("interjection_chance", 0.5):
        text = f"{style['interjections'][np.random.randint(len(style['interjections']))]} {text}"
    if style.get("laugh_marker"):
        # смех внутри речи: «ха-ха» → метка 🤣 (её адаптер отыгрывает живым смехом на этом месте)
        marked_text = LAUGH_RX.sub(f" {LAUGH_MARK} ", text) if LAUGH_RX.search(text) else text
        if LAUGH_MARK not in marked_text and np.random.rand() < style.get("marker_chance", 0.5):
            # в самое начало метка иногда давала тишину (28.09) — после первого оборота: запятая в первой половине,
            # иначе после второго слова
            comma = marked_text.find(",")
            if 0 < comma < len(marked_text) * 0.5:
                marked_text = f"{marked_text[:comma]} {LAUGH_MARK}{marked_text[comma:]}"
            else:
                words = marked_text.split(" ")
                cut = min(2, max(1, len(words) - 1))
                marked_text = " ".join(words[:cut] + [LAUGH_MARK] + words[cut:])
        return synth_one(re.sub(r"\s{2,}", " ", marked_text).strip(), emotion)
    if not _banks.get("laugh") and LAUGH_RX.search(text):
        # живого смеха в банке нет — «ха-ха» не читать слогами (друг: «как будто текстом смеётся»), а просто убрать;
        # смеётся манера [смех]
        text = re.sub(r"\s{2,}", " ", re.sub(r"«\s*»|\"\s*\"", "", LAUGH_RX.sub("", text))).strip(" ,;")
        if not re.search(r"[а-яёa-z]", text, re.IGNORECASE):
            return np.zeros(int(0.25 * 24000), dtype=np.float32), 24000, 0.0
    laughs_in_text = bool(_banks.get("laugh")) and LAUGH_RX.search(text) is not None
    lead = bool(kind) and bool(_banks.get(kind)) and np.random.rand() < style.get("bank_chance", 0.5)
    if not laughs_in_text and not lead:
        return synth_one(text, emotion)
    parts: list[str | tuple[str]] = []   # кортеж (вид,) — место живого звука
    pos = 0
    if laughs_in_text:
        for m in LAUGH_RX.finditer(text):
            before = text[pos:m.start()].strip(" ,;")
            if before:
                parts.append(before)
            parts.append(("laugh",))
            pos = m.end()
    tail = text[pos:].strip(" ,;")
    if tail:
        parts.append(tail)
    if lead and not (parts and isinstance(parts[0], tuple)):
        parts.insert(0, (kind,))
    loud = (style or _emotions.get("calm") or {}).get("loud_db")
    sr, took, out = 24000, 0.0, []
    spoken = [synth_one(t, emotion) if isinstance(t, str) else None for t in parts]
    for s_ in spoken:
        if s_ is not None:
            sr = s_[1]
    used = []
    for t, s_ in zip(parts, spoken):
        if s_ is None:
            clip = bank_clip(t[0], sr)
            if clip is not None:
                out += [level(clip, sr, loud), np.zeros(int(0.08 * sr), dtype=np.float32)]
                used.append(t[0])
        else:
            out.append(s_[0])
            took += s_[2]
    if not out:
        return synth_one(text, emotion)
    log(f"живые звуки: {', '.join(used)} | {text[:60]}")
    return np.concatenate(out), sr, took


def synth_one(text: str, emotion: str = "") -> tuple[np.ndarray, int, float]:
    to_model, marked = prepare(text)
    if not to_model:
        to_model = "Мур."
    style = _emotions.get(emotion) or _emotions.get("calm") or {}
    ref, ref_text = style.get("ref", _ref), style.get("ref_text", _ref_text)
    manner = {"instruct": style["instruct"]} if "instruct" in style else {}
    trained = bool(style.get("xvec"))
    lora = trained or bool(style.get("lora"))
    # шипящее междометие в начале («Тс-с…», «Пфф…», «Шш…») обученный адаптер «договаривает» тишиной (28.09: оба раза 0 с
    # звука на шёпоте и раздражении) — такие фразы сразу манерой по эталону, без адаптера
    if lora and HISS_RX.match(text):
        trained = lora = False
    # потолок длины: ~8 знаков в секунду (живая речь 12–15) + 1,5 с — застрявшая в тишине модель не жжёт 8 с впустую
    cap_s = len(text) / 8 + 1.5 + 1.2 * text.count(LAUGH_MARK)   # смех по метке 🤣 — ещё ~секунда на каждую
    frames = int(12 * cap_s)

    def take(temperature: float, top_k: int, top_p: float, adapter: bool, xvec: bool) -> Take:
        switch = _emo.set(adapter) if _emo is not None else 0.0
        if switch > 0:
            log(f"эмоции: адаптер {'включён' if _emo.on else 'выключен'} за {switch * 1000:.0f} мс")
        # обученная эмоция — как учили: отпечаток klava_02 + манера, непотоково; иначе клон по эталону (+ манера)
        extra = dict(manner, xvec_only=True, non_streaming_mode=True) if xvec else manner
        wavs, sr_ = _model.generate_voice_clone(
            text=to_model, language="Russian", ref_audio=ref, ref_text=ref_text,
            temperature=temperature, top_k=top_k, top_p=top_p, do_sample=True, max_new_tokens=frames, **extra,
        )
        # свой потолок тона у стиля: смех по природе выше речи (28.09: страховка «лолька» переговаривала его без смеха)
        return Take(np.asarray(wavs[0], dtype=np.float32), sr_, len(text), cap_s, style.get("max_f0", MAX_F0),
                    style.get("min_f0", MIN_F0))

    with _lock:
        t = time.time()
        # temperature 0.55 и top_k 20 — интонация живая, но без «фантазий» с ударением
        best = take(0.55, 20, 0.9, lora, trained)
        if best.bad:
            # манера иногда выдёргивает тон вверх, в «лольку» (28.09: раздражение 231 Гц, в другой раз 387), а обученные
            # шёпот и грусть изредка застревают в тишине (на «Тс-с…» — 9 с тишины перед 2 с шёпота) — ещё раз, спокойнее.
            # Сдвиг тона вокодером не годится: металл и «робот» в голосе
            again = take(0.4, 15, 0.85, lora, trained)
            log(f"[{emotion or 'calm'}] {best.why()} — переговорила: {again.why()}, тон {again.f0:.0f} Гц")
            if again.key() < best.key():
                best = again
            if best.severe and lora:
                # обученная эмоция дважды сломала речь — по-старому: исходные веса, клон по эталону + манера
                old = take(0.55, 20, 0.9, False, False)
                log(f"обученная [{emotion}] не справилась — манерой без адаптера: {old.why()}, тон {old.f0:.0f} Гц")
                if old.key() < best.key():
                    best = old
        wav, sr, f0 = best.wav, best.sr, best.f0
        took = time.time() - t
    wav = level(tidy(wav, sr), sr, style.get("loud_db"), emotion or "calm")
    dur = len(wav) / sr
    log(f"синтез {len(text)} симв [{emotion or 'calm'}] -> {dur:.2f} с аудио, тон {f0:.0f} Гц, за {took:.2f} с (RTF {took / max(dur, 1e-3):.2f}) | {marked}")
    return wav, sr, took


STREAM_FIRST = 5    # кадров кодека (12 в секунду) до первого куска: первый звук через ~0,4–0,5 с
STREAM_EVERY = 12   # дальше — кусками по секунде: декодер кодека на каждом куске пересчитывает 25 кадров контекста,
                    # и мелкие куски съедали запас скорости (RTF потока 1,1 против 0,8 целиком, проба 29.09)


def voice_stream(text: str, ref: str, ref_text: str, instruct: str | None, frames: int, temperature: float = 0.55):
    """
    Поток звука клона (ICL по эталону) с неровным графиком декодера: первый кусок после STREAM_FIRST кадров, дальше —
    по STREAM_EVERY. Генерация — faster-qwen3-tts (CUDA-графы) кусками по 4 кадра; склейка кусков — как у него самого:
    сначала накопительная раскодировка (с кодами эталона впереди — у декодера акустический контекст), потом окно с
    25 кадрами левого контекста. Отдаёт float32-куски звука 24 кГц.
    """
    from faster_qwen3_tts.streaming import fast_generate_streaming
    with torch.inference_mode():
        m, talker, config, tie, tam, tth, tpe, ref_codes = _model._prepare_generation(
            text=text, language="Russian", ref_audio=ref, ref_text=ref_text, xvec_only=False, non_streaming_mode=False,
            append_silence=True, voice_clone_prompt=None, instruct=instruct)
        tok = m.speech_tokenizer
        ctx = 25
        codes, pending, done_frames, prev_len, spf = [], 0, 0, 0, None
        gen = fast_generate_streaming(
            talker=talker, talker_input_embeds=tie, attention_mask=tam, trailing_text_hiddens=tth, tts_pad_embed=tpe,
            config=config, max_new_tokens=frames, min_new_tokens=2, temperature=temperature, top_k=20, top_p=0.9, do_sample=True,
            repetition_penalty=1.05, chunk_size=4, predictor_graph=_model.predictor_graph, talker_graph=_model.talker_graph)

        def decode() -> np.ndarray:
            nonlocal prev_len, spf
            flat = torch.cat(codes, dim=0)
            n_total = flat.shape[0]
            if spf is None:
                # у декодера левый контекст — хвост эталона (25 кадров хватает, весь эталон ~10 с раскодировать долго:
                # первый кусок из-за этого шёл 1,1 с)
                tail = ref_codes[-ctx:].to(flat.device) if ref_codes is not None else None
                inp = torch.cat([tail, flat], dim=0) if tail is not None else flat
                wavs, _sr = tok.decode({"audio_codes": inp.unsqueeze(0)})
                a = wavs[0]
                a = a.flatten().cpu().numpy() if hasattr(a, "cpu") else np.asarray(a).flatten()
                if tail is not None:
                    a = a[int(tail.shape[0] / max(inp.shape[0], 1) * len(a)):]
                new = a[prev_len:]
                prev_len = len(a)
                if n_total >= ctx:
                    spf = len(a) / n_total
                return new
            start = max(0, n_total - pending - ctx)
            wavs, _sr = tok.decode({"audio_codes": flat[start:].unsqueeze(0)})
            a = wavs[0]
            a = a.flatten().cpu().numpy() if hasattr(a, "cpu") else np.asarray(a).flatten()
            n_ctx = flat.shape[0] - start - pending
            return a[int(round(n_ctx * spf)):] if n_ctx > 0 else a

        try:
            for chunk, _timing in gen:
                codes.append(chunk)
                pending += chunk.shape[0]
                if pending >= (STREAM_FIRST if done_frames == 0 else STREAM_EVERY):
                    yield decode()
                    done_frames += pending
                    pending = 0
            if pending:
                yield decode()
        finally:
            gen.close()


def stream_ok(text: str, style: dict) -> bool:
    """Поток — для манер, эталонов и эмоций с LoRA поверх эталона. Целиком — «только отпечаток» (xvec) и смех внутри
    речи: у них срывы чаще, а чинит их только повторный синтез. Срыв LoRA в потоке — тишина вместо речи — ловится в
    первые 1,2 с, и фраза уходит обычным путём (с проверкой и повтором), см. synth_stream."""
    if style.get("xvec") or style.get("laugh_marker"):
        return False
    return not (_banks.get("laugh") and LAUGH_RX.search(text))


def synth_stream(text: str, emotion: str, emit) -> float:
    """
    Фраза кусками: emit(float32 моно, sr) на каждый готовый кусок. Живой звук банка (вздох, «ах!») — первым куском,
    он записан заранее. Громкость — усилением стиля из прошлых фраз, пик мягко ограничен. Тишина в начале срезается,
    застрявшую в тишине модель (1,3 с тишины после речи) — останавливаем. Возвращает секунды звука.
    Тон под присмотром (друг 10.10: «грубый мужской голос»): ниже порога в начале — попытка выбрасывается до колонок.
    """
    global _cps
    style = _emotions.get(emotion) or {}
    if not stream_ok(text, style):
        wav, sr, _ = synth(text, emotion)
        emit(wav, sr)
        return len(wav) / sr
    sent = 0.0
    kind = style.get("bank")
    if kind and _banks.get(kind) and np.random.rand() < style.get("bank_chance", 0.5):
        clip = bank_clip(kind, 24000)
        if clip is not None:
            loud = (style or _emotions.get("calm") or {}).get("loud_db")
            emit(level(clip, 24000, loud), 24000)
            emit(np.zeros(int(0.08 * 24000), dtype=np.float32), 24000)
            sent += len(clip) / 24000 + 0.08
            log(f"живые звуки (поток): {kind} | {text[:60]}")
    to_model, marked = prepare(text)
    if not to_model:
        to_model = "Мур."
    ref, ref_text = style.get("ref", _ref), style.get("ref_text", _ref_text)
    manner = {"instruct": style["instruct"]} if "instruct" in style else {}
    cap_s = len(text) / 8 + 1.5
    name = emotion or "calm"
    calm_loud = (_emotions.get("calm") or {}).get("loud_db")
    loud = style.get("loud_db", calm_loud)
    gain = _gain_est.get(name)
    if gain is None:
        base = _gain_est.get("calm", 1.0)
        gain = base * (10 ** (((loud or 0) - (calm_loud or 0)) / 20)) if loud is not None and calm_loud is not None else base
    # эмоция с LoRA: адаптер на время фразы (шипящее начало «Тс-с», «Пфф» адаптер глушит — такие без него; стиль, где
    # адаптер раз за разом уводит тон вниз, — на время без него, см. lora_note)
    lora = (bool(style.get("lora")) and _emo is not None and not HISS_RX.match(text)
            and time.time() >= _lora_off_until.get(emotion or "calm", 0.0))
    min_f0 = style.get("min_f0", MIN_F0)
    sr = 24000
    first = None
    t = time.time()

    def attempt(use_lora: bool, temperature: float) -> tuple[str, float, list[np.ndarray], float]:
        """Одна попытка потока: (итог, тон, куски, секунды отданного в колонки). Итог: ok | silent (модель молчит) |
        low_head (тон ниже порога в начале — НИ ЗВУКА не отдано) | low_mid (сорвалась вниз посреди фразы — дальше не
        отдаём). Первые куски держит, пока не набралось 3 кадра с тоном (120 мс речи) или 1,6 с звука: «грубый
        мужской» голос отбрасывается до колонок, а не после."""
        nonlocal first
        pieces: list[np.ndarray] = []
        held: list[np.ndarray] = []
        head, checked, quiet_run, head_silence, out_s = True, False, 0.0, 0.0, 0.0
        if _emo is not None:
            _emo.set(use_lora)
        gen = voice_stream(to_model, ref, ref_text, manner.get("instruct"), int(12 * cap_s), temperature)

        def send(a: np.ndarray) -> None:
            nonlocal first, out_s
            out = np.tanh(a * gain * 1.05) / 1.05 if gain * float(np.max(np.abs(a))) > 0.9 else a * gain
            emit(out.astype(np.float32), sr)
            if first is None:
                first = time.time() - t
            out_s += len(a) / sr

        try:
            for audio in gen:
                a = np.asarray(audio, dtype=np.float32).reshape(-1)
                if a.size == 0:
                    continue
                if head:
                    # тишина в начале — до 60 мс
                    fr = int(0.02 * sr)
                    n = len(a) // fr
                    rms = np.sqrt(np.mean(a[:n * fr].reshape(n, fr) ** 2, axis=1)) if n else np.zeros(0)
                    loud_idx = np.nonzero(rms > 0.004)[0]
                    if len(loud_idx) == 0:
                        head_silence += len(a) / sr
                        if head_silence > 1.2:
                            # срыв (у LoRA бывает): модель молчит — не тянем тишину, фраза пойдёт обычным путём
                            return "silent", 0.0, pieces, out_s
                        continue
                    a = a[max(0, loud_idx[0] * fr - int(0.06 * sr)):]
                    head = False
                pieces.append(a)
                if not checked:
                    held.append(a)
                    h = np.concatenate(held)
                    frames = f0_frames(h, sr)
                    # решаем по первому куску, если в нём хоть 3 звонких кадра: её ~300 Гц и «мужик» 150–200 Гц
                    # различаются с запасом. С 6 кадрами часть фраз ждала второй кусок: 10.10 первый звук 0,64 с, когда
                    # хватило первого, и 0,85–0,95 с — когда ждала. На её записях 3 кадра против 6: ниже порога 2 из 77
                    # против 1 из 77, Кот 17 из 20 против 18, Паша 8 из 13 против 7
                    if len(frames) < 3 and len(h) < 1.6 * sr:
                        continue   # голоса ещё мало — держим, не отдаём
                    checked = True
                    f0 = float(np.median(frames)) if len(frames) >= 3 else 0.0
                    if 0 < f0 < min_f0:
                        return "low_head", f0, pieces, out_s
                    for x in held:
                        send(x)
                    held = []
                else:
                    # посреди фразы — строже: живая интонация к концу фразы опускается, а срыв в «мужика» — это
                    # 150–200 Гц, далеко внизу
                    frames = f0_frames(a, sr)
                    if len(frames) >= 10 and float(np.median(frames)) < min_f0 - 25:
                        return "low_mid", float(np.median(frames)), pieces[:-1], out_s
                    send(a)
                # застряла в тишине: речь уже была, а последние 1,3 с — тишина
                fr = int(0.05 * sr)
                n = len(a) // fr
                rms = np.sqrt(np.mean(a[:n * fr].reshape(n, fr) ** 2, axis=1)) if n else np.zeros(0)
                quiet_run = quiet_run + len(a) / sr if n and rms.max() < 0.004 else 0.0
                if quiet_run > 1.3 and out_s > 1.0:
                    log(f"поток: 1,3 с тишины — стоп | {text[:50]}")
                    break
        finally:
            gen.close()
        if held:
            # фраза короче, чем нужно для замера с запасом, — решаем по тому, что есть
            frames = f0_frames(np.concatenate(held), sr)
            f0 = float(np.median(frames)) if len(frames) >= 3 else 0.0
            if 0 < f0 < min_f0:
                return "low_head", f0, pieces, out_s
            for x in held:
                send(x)
        return "ok", 0.0, pieces, out_s

    tag = f"{name}{'+LoRA' if lora else ''}"
    with _lock:
        status, f0, pieces, out_s = attempt(lora, 0.55)
        if lora:
            lora_note(name, status in ("low_head", "low_mid"))
        if status == "low_head":
            # адаптер учился и на мужских голосах RESD — повтор без него; без адаптера и так — спокойнее
            log(f"поток [{tag}]: тон {f0:.0f} Гц ниже {min_f0:.0f} в начале — не её голос, в колонки не пустила, "
                f"заново {'без адаптера' if lora else 'спокойнее'} | {text[:50]}")
            status, f0, pieces, out_s = attempt(False, 0.45)
            tag = name
        took = time.time() - t
    sent += out_s
    if status == "silent":
        log(f"поток [{tag}]: 1,2 с тишины вместо речи — обычным синтезом | {text[:50]}")
        wav, sr_, _ = synth_one(text, emotion)   # живой звук банка уже прозвучал — только речь, с проверкой
        emit(wav, sr_)
        return sent + len(wav) / sr_
    if status == "low_head":
        log(f"поток [{tag}]: и второй раз тон {f0:.0f} Гц — обычным синтезом с проверкой | {text[:50]}")
        wav, sr_, _ = synth_one(text, emotion)
        emit(wav, sr_)
        return sent + len(wav) / sr_
    if status == "low_mid":
        # сорвалась вниз посреди фразы: сказанное не вернуть — остаток с первого непроизнесённого слова (по слуху),
        # спокойным голосом с проверкой
        said = np.concatenate(pieces) if pieces else np.zeros(0, dtype=np.float32)
        rest, how = rest_after(text, said, sr, out_s)
        log(f"поток [{tag}]: посреди фразы тон упал до {f0:.0f} Гц — стоп, остаток спокойным голосом [{how}]: «{rest[:50]}»")
        if len(rest) >= 3:
            wav, sr_, _ = synth_one(rest, "")
            emit(wav, sr_)
            sent += len(wav) / sr_
        return sent
    if pieces:
        full = np.concatenate(pieces)
        level(full, 24000, loud, name)   # запомнить настоящее усиление стиля на следующую фразу
        dur = len(full) / 24000
        if dur > 1.0:
            _cps = 0.8 * _cps + 0.2 * (len(text) / dur)
        tone = median_f0(full[:int(4 * 24000)], 24000)
        log(f"поток {len(text)} симв [{tag}] -> {dur:.2f} с аудио, тон {tone:.0f} Гц, первый звук через {first or 0:.2f} с, "
            f"всё за {took:.2f} с (RTF {took / max(dur, 1e-3):.2f}) | {marked}")
    return sent


# знаков в секунду её речи (по прошлым фразам): по нему — где оборвать фразу, сорвавшуюся вниз посреди
_cps = 14.5


def _stt_url() -> str:
    """Адрес слуха Клавы (whisper-server): KLAVA_STT_URL или sttUrl из klava.json рядом; по умолчанию 8097."""
    url = os.environ.get("KLAVA_STT_URL", "")
    if not url:
        try:
            url = json.loads((ROOT / "klava.json").read_text(encoding="utf-8")).get("sttUrl") or ""
        except (OSError, ValueError):
            url = ""
    return (url or "http://127.0.0.1:8097").rstrip("/")


_WORD_RX = re.compile(r"[а-яёa-z0-9]+(?:-[а-яёa-z0-9]+)*", re.IGNORECASE)


def _norm_words(s: str) -> list[str]:
    return [w.lower().replace("ё", "е") for w in _WORD_RX.findall(s.replace("+", "").replace("́", ""))]


def _heard(audio: np.ndarray, sr: int) -> str | None:
    """Что уже прозвучало — распознаёт слух Клавы (whisper-server на видеокарте, ~0,3 с на пару секунд речи).
    whisper.cpp читает WAV только 16 кГц — пересэмплируем линейно (для распознавания хватает). None — слух не ответил."""
    if audio.size < sr // 4:
        return ""
    n16 = int(len(audio) * 16000 / sr)
    x16 = np.interp(np.linspace(0, len(audio) - 1, n16), np.arange(len(audio)), audio).astype(np.float32)
    buf = io.BytesIO()
    sf.write(buf, np.clip(x16, -1, 1), 16000, format="WAV", subtype="PCM_16")
    boundary = f"----klava{int(time.time() * 1000)}"
    parts = []
    for name, value in (("response_format", "json"), ("temperature", "0.0"), ("language", "ru")):
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode())
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"said.wav\"\r\n"
                 f"Content-Type: audio/wav\r\n\r\n".encode() + buf.getvalue() + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    req = urllib.request.Request(f"{_stt_url()}/inference", data=b"".join(parts), method="POST",
                                 headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        with urllib.request.urlopen(req, timeout=3.0) as r:
            return str(json.loads(r.read().decode("utf-8", "replace")).get("text") or "")
    except (OSError, ValueError):
        return None


def rest_after(text: str, said: np.ndarray, sr: int, out_s: float) -> tuple[str, str]:
    """
    Остаток фразы после обрыва посреди неё: (остаток, как найден). Раньше место обрыва угадывалось по среднему темпу
    (секунды × знаков в секунду) и откатывалось к ПРЕДЫДУЩЕМУ пробелу — на стыке слово повторялось («ключом на 9…
    ключом»), а при ошибке в другую сторону кусок проглатывался (друг 18.11: «то повторы, то затыки»).
    Теперь прозвучавшее распознаётся слухом и выравнивается по словам текста: остаток — с первого непроизнесённого
    слова; слово, оборванное на середине, повторяется целиком (как живая самопоправка). Слух не ответил или распознал
    не то — запасной путь: граница слова, БЛИЖАЙШАЯ к оценке по темпу (а не всегда предыдущая).
    """
    spans = [(m.start(), m.end(), m.group(0).lower().replace("ё", "е")) for m in _WORD_RX.finditer(text)]
    if not spans:
        return "", "пусто"
    heard = _heard(said, sr)
    if heard is not None:
        hw = _norm_words(heard)
        if not hw:
            return text.strip(), "слух: ничего не прозвучало"
        tw = [w for _, _, w in spans]
        best_k, best_r = -1, 0.0
        for k in range(max(1, len(hw) - 2), min(len(tw), len(hw) + 2) + 1):
            r = difflib.SequenceMatcher(None, " ".join(tw[:k]), " ".join(hw)).ratio()
            if r > best_r:
                best_k, best_r = k, r
        if best_k > 0 and best_r >= 0.6:
            k = best_k
            last_t, last_h = tw[k - 1], hw[-1]
            # последнее услышанное — обрывок слова текста («клю» от «ключом»): слово не договорено, повторить целиком
            if len(last_h) < len(last_t) and difflib.SequenceMatcher(None, last_t, last_h).ratio() < 0.8:
                k -= 1
            if k >= len(spans):
                return "", f"слух: договорила всё ({best_r:.2f})"
            return text[spans[k][0]:].strip(), f"слух: «{' '.join(hw[-3:])}» ({best_r:.2f})"
    # запасной путь: оценка по темпу, ближайшая граница слова
    spoken = out_s * _cps
    starts = [s for s, _, _ in spans]
    k = min(range(len(starts)), key=lambda i: abs(starts[i] - spoken))
    return text[starts[k]:].strip(), "по темпу" + (" (слух не ответил)" if heard is None else " (слух не совпал)")

# Стиль, у которого адаптер уводит тон вниз раз за разом, на время говорит без адаптера. 10.10: [sly+LoRA] — 3 фразы
# из 7 (224 и 208 Гц в начале, 198 посреди); каждый срыв — повтор без адаптера и +1–2 с до первого звука.
LORA_OFF_S = 15 * 60
_lora_low: dict[str, list[bool]] = {}
_lora_off_until: dict[str, float] = {}


def lora_note(name: str, low: bool) -> None:
    """Итог фразы стиля с адаптером: 2 низких из 4 последних — стиль без адаптера на LORA_OFF_S."""
    hist = _lora_low.setdefault(name, [])
    hist.append(low)
    del hist[:-4]
    if sum(hist) >= 2:
        hist.clear()
        _lora_off_until[name] = time.time() + LORA_OFF_S
        log(f"эмоции: [{name}] с адаптером уводит тон вниз (2 из 4 последних фраз) — {LORA_OFF_S // 60} мин без адаптера")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # свой лог вместо стандартного шума
        pass

    def _send(self, code: int, body: bytes, ctype: str) -> None:
        try:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (ConnectionError, BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.close_connection = True

    def _body(self) -> bytes:
        length = int(self.headers.get("Content-Length", "0") or 0)
        return self.rfile.read(length) if length > 0 else b""

    def do_GET(self):
        if self.path == "/health":
            self._send(200 if _ready else 503, b"ok" if _ready else b"loading", "text/plain")
        elif self.path == "/emotions":
            info = {k: ("эталон " + Path(v["ref"]).name if "ref" in v else "обучена" if v.get("xvec") else "обучена+эталон" if v.get("lora")
                        else "манера" if "instruct" in v else "как есть") for k, v in _emotions.items()}
            self._send(200, json.dumps(info, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")
        else:
            self._send(404, b"not found", "text/plain")

    def do_POST(self):
        try:
            raw = self._body()   # тело читается всегда: иначе остаток запроса сломал бы следующий на том же соединении
            if self.path == "/reload":
                n = ru_text.reload_custom()
                log(f"свой словарь ударений перечитан: {n} слов")
                e = load_emotions()
                self._send(200, json.dumps({"words": n, "emotions": e}).encode("utf-8"), "application/json")
                return
            if self.path == "/accent":
                # тренажёр чтения: ударения в тексте как есть (видеокарту не трогает, замок — только акцентуатора)
                if ru_text._acc is None:
                    self._send(503, b"accent loading", "text/plain")
                    return
                req = json.loads(raw.decode("utf-8") or "{}")
                text = str(req.get("text", ""))[:6000]
                t = time.time()
                marked = ru_text.accent(text)
                log(f"ударения для чтения: {len(text)} симв за {time.time() - t:.2f} с")
                self._send(200, json.dumps({"text": marked}, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")
                return
            if self.path == "/tts_stream":
                self._stream(raw)
                return
            if self.path != "/tts":
                self._send(404, b"not found", "text/plain")
                return
            if not _ready:
                self._send(503, b"loading", "text/plain")
                return
            req = json.loads(raw.decode("utf-8") or "{}")
            text = str(req.get("text", "")).strip()
            if not text:
                self._send(400, b"empty text", "text/plain")
                return
            wav, sr, _ = synth(text[:600], str(req.get("emotion", "") or ""))
            buf = io.BytesIO()
            sf.write(buf, wav, sr, format="WAV", subtype="PCM_16")
            self._send(200, buf.getvalue(), "audio/wav")
        except (ConnectionError, BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.close_connection = True
        except Exception as e:  # клиенту — текст ошибки, серверу — жить дальше
            log(f"ошибка: {e!r}")
            self._send(500, str(e).encode("utf-8"), "text/plain; charset=utf-8")


    def _stream(self, raw: bytes) -> None:
        """Ответ кусками (chunked): PCM 16 бит моно по мере синтеза. Ошибка посреди — поток просто кончается."""
        if not _ready:
            self._send(503, b"loading", "text/plain")
            return
        req = json.loads(raw.decode("utf-8") or "{}")
        text = str(req.get("text", "")).strip()[:600]
        if not text:
            self._send(400, b"empty text", "text/plain")
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("X-Sample-Rate", "24000")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

        def emit(wav: np.ndarray, sr: int) -> None:
            if sr != 24000:
                import librosa
                wav = librosa.resample(wav, orig_sr=sr, target_sr=24000)
            data = (np.clip(wav, -1.0, 1.0) * 32767).astype("<i2").tobytes()
            if data:
                self.wfile.write(f"{len(data):X}\r\n".encode("ascii") + data + b"\r\n")
                self.wfile.flush()

        try:
            synth_stream(text, str(req.get("emotion", "") or ""), emit)
        except (ConnectionError, BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.close_connection = True
            return
        except Exception as e:  # поток уже идёт — код ошибки не отправить; кончаем поток, клиент возьмёт /tts
            log(f"ошибка потока: {e!r}")
        try:
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (ConnectionError, BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            self.close_connection = True


class TtsServer(ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        exc = sys.exception()
        if isinstance(exc, (ConnectionError, BrokenPipeError, ConnectionResetError, ConnectionAbortedError)):
            return
        log(f"сетевой сбой: {exc!r}")


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
    ap.add_argument("--model", default="")
    ap.add_argument("--bench", action="store_true")
    ap.add_argument("--emo-lora", default="", help="другая эмоциональная LoRA (проба новой, не трогая рабочую models/emo_lora)")
    args = ap.parse_args()
    if args.emo_lora:
        global EMO_LORA
        EMO_LORA = Path(args.emo_lora).resolve()
    model_dir = Path(args.model) if args.model else (
        STRESS_MODEL if (STRESS_MODEL / "model.safetensors").exists() else PLAIN_MODEL)
    load(model_dir)
    if args.bench:
        bench()
        return
    srv = TtsServer(("127.0.0.1", args.port), Handler)
    log(f"голос Клавы слушает http://127.0.0.1:{args.port}")
    srv.serve_forever()


if __name__ == "__main__":
    sys.exit(main())
