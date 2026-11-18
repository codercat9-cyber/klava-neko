"""ACE-Step 1.5 для каверов Клавы. Запускается ИЗ СВОЕГО окружения (tools\\ace-step\\.venv — у ACE свои torch и
transformers), сервер музыки зовёт его отдельным процессом:

    tools\\ace-step\\.venv\\Scripts\\python.exe music\\ace_gen.py <запрос.json>

Запрос: {"parts": [{"name": "p0", "src": кусок трека, "lyrics": текст с метками частей}, …], "full": весь трек (для
         описания стиля), "language": "ru", "strength": 1.0, "noise": 0.0, "keyscale": "A minor", "out": папка,
         "runs": [{"model": "acestep-v15-turbo", "seeds": [..]}, {"model": "acestep-v15-sft", "seeds": [..],
                   "steps": 50, "cfg": 7.0}] (старое "seeds": [..] — только turbo; сиды у моделей разные),
         "caption": "" (пусто — стиль опишет 5Hz LM ACE, прослушав оригинал), "vocal": "soft female vocal"}
Печать построчно: «P <0..1> <что делаю>» — ход; «C <описание>» — стиль; «W <кусок> <сид> <путь>» — готовый вариант;
«OK» — всё.

Почему так (замер 30.09 на «Lately»): речь Клавы, растянутая по нотам (WORLD), упирается в потолок — «глючно и
отрывками»; ACE поёт живым вокалом по смысловым кодам оригинала (форма, мелодия, тайминг до 20–40 мс) новый текст.
Сила 1.0 — все шаги диффузии по оригиналу (0.8 — к концу уплывает на 3–5 полутонов). Кусками до ~110 с: вся песня
(237 с) целиком уезжала на тритон и мимо мелодии (22 % кадров в полутоне), те же 100 с отдельно — без сдвига, 52 %.
Сиды сильно разнятся по разборчивости (0.14 против 0.39 ошибок по словам) — вариантов несколько, лучший выбирает судья.
Модели (карточка ACE 1.5): turbo — 8 шагов, качество «очень высокое», без CFG; sft — 50 шагов, «высокое», с CFG (строже
держится текста) — поют обе, судья берёт лучшее. noise — cover_noise_strength: диффузия стартует не с чистого шума, а
с зашумлённого оригинала (0 — шум, 1 — сам оригинал): мелодия певицы сохраняется точнее, чем по смысловым кодам 5 Гц.
"""
from __future__ import annotations

import json
import os
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "tools" / "ace-step"


def say(*a) -> None:
    print(*a, flush=True)


def style_only(caption: str, keep: int = 3) -> str:
    """Из описания LM — только аранжировка и настроение: фразы про голос выкидываются (замер 30.09: LM назвал вокал Леры
    Линн «мужским, надрывным»), длинное описание режется до keep фраз, чтобы голос Клавы (в начале описания) не отрезало
    лимитом."""
    import re
    parts = [p.strip() for p in re.split(r"(?<=[.!?])\s+", caption.strip()) if p.strip()]
    voiceless = [p for p in parts if not re.search(r"vocal|voice|sing|singer|\bmale\b|female|lyric|rap|choir|harmon", p, re.I)]
    out = " ".join((voiceless or parts)[:keep])
    return re.sub(r"\s+", " ", out).strip()


def load(model: str):
    """DiT ACE нужной модели (turbo / sft) — выгрузка на CPU между шагами, рядом живёт голос Клавы."""
    from acestep.handler import AceStepHandler
    dit = AceStepHandler()
    msg, ok = dit.initialize_service(project_root=str(ROOT), config_path=model, device="cuda",
                                     offload_to_cpu=True, offload_dit_to_cpu=True)
    return dit if ok else None, msg


def main() -> int:
    req = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    os.chdir(ROOT)
    sys.path.insert(0, str(ROOT))
    import gc

    import torch
    from acestep.inference import GenerationConfig, GenerationParams, generate_music, understand_music
    from acestep.llm_inference import LLMHandler

    out = Path(req["out"])
    out.mkdir(parents=True, exist_ok=True)
    runs = req.get("runs") or [{"model": "acestep-v15-turbo", "seeds": [int(s) for s in req.get("seeds") or [7]]}]
    runs = [r for r in runs if (ROOT / "checkpoints" / r["model"]).is_dir() and r.get("seeds")]
    if not runs:
        say("E нет ни одной модели ACE в checkpoints")
        return 2
    parts = req["parts"]
    # что уже спето в прошлые заходы (процесс ACE падает посреди) — не перепевать; модель грузится, только если ей есть
    # работа: замер 30.09 — sft сразу после turbo в том же процессе не встала («недостаточно системных ресурсов», os
    # error 1450), а в свежем процессе грузится первой
    total = sum(len(r["seeds"]) for r in runs) * len(parts)
    k = 0
    todos = []
    for run in runs:
        todo = []
        for part in parts:
            for seed in run["seeds"]:
                dst = out / part["name"] / f"seed{seed}"
                done = sorted(dst.glob("*.wav")) if dst.is_dir() else []
                done = [p for p in done if not p.name.startswith(("ace_", "klava_"))]
                if done:
                    k += 1
                    say(f"W {part['name']} {seed} {done[0]}")
                else:
                    todo.append((part, seed, dst))
        todos.append(todo)
    need_lm = not (req.get("caption") or "").strip()
    first = next((r for r, td in zip(runs, todos) if td), None)
    if first is None and not need_lm:
        say("OK")
        return 0
    loaded = (first or runs[0])["model"]
    say(f"P 0.02 гружу ACE-Step ({loaded})…")
    t = time.time()
    dit, msg = load(loaded)
    if dit is None:
        say(f"E ACE-Step не поднялся: {msg[:300]}")
        return 2
    say(f"P 0.08 ACE-Step готов за {time.time() - t:.0f} с")
    llm = LLMHandler()
    given = (req.get("caption") or "").strip()
    caption = given
    if not caption:
        say("P 0.1 слушаю оригинал: стиль, инструменты, подача…")
        try:
            lm_msg, lm_ok = llm.initialize(checkpoint_dir=str(ROOT / "checkpoints"), lm_model_path="acestep-5Hz-lm-1.7B",
                                           backend="pt", device="cuda", offload_to_cpu=True)
            if lm_ok:
                codes = dit.convert_src_audio_to_codes(req.get("full") or parts[0]["src"])
                if codes and not codes.startswith("❌"):
                    u = understand_music(llm, codes, temperature=0.3)
                    if u.success and u.caption:
                        caption = style_only(u.caption.strip())
            else:
                say(f"P 0.12 LM ACE не поднялся ({lm_msg[:120]}) — стиль по меткам слуха")
        except Exception as e:  # noqa: BLE001 — стиль не главное, кавер всё равно споётся
            say(f"P 0.12 LM ACE не описал стиль ({type(e).__name__}: {e}) — стиль по меткам слуха")
        finally:
            llm.unload()   # кавер LM не нужен: мелодию и форму даёт сам оригинал
    if not caption:
        caption = req.get("fallback_caption") or "emotional song"
    vocal = (req.get("vocal") or "").strip()
    lang = req.get("language") or "ru"
    head = vocal + (" singing in Russian" if lang == "ru" else "")
    # второй круг получает уже готовое описание — голос в нём стоит первым, второй раз не приклеивать
    caption = (f"{head}. {caption}" if head.strip() and not caption.lower().startswith(head.lower()) else caption)[:480]
    say(f"C {caption}")
    for run, todo in zip(runs, todos):
        if not todo:
            continue
        if run["model"] != loaded or dit is None:   # другая модель: прежнюю выгрузить целиком, видеокарта одна на двоих с голосом Клавы
            del dit
            gc.collect()
            torch.cuda.empty_cache()
            say(f"P {0.15 + 0.85 * k / total:.3f} гружу ACE-Step ({run['model']})…")
            dit, msg = load(run["model"])
            loaded = run["model"]
            if dit is None:
                say(f"P {0.15 + 0.85 * k / total:.3f} {run['model']} не поднялся: {msg[:200]} — пропускаю")
                k += len(todo)
                continue
        extra = {"inference_steps": int(run["steps"]), "guidance_scale": float(run.get("cfg", 7.0))} if run.get("steps") else {}
        for part, seed, dst in todo:
            k += 1
            say(f"P {0.15 + 0.85 * (k - 1) / total:.3f} пою кусок {part['name']} ({k}/{total}, {run['model'].split('-')[-1]}"
                f"{' от оригинала' if run.get('noise') else ''})")
            params = GenerationParams(task_type="cover", src_audio=part["src"], caption=caption, lyrics=part["lyrics"],
                                      vocal_language=lang, audio_cover_strength=float(req.get("strength", 1.0)),
                                      cover_noise_strength=float(run.get("noise", req.get("noise", 0.0))),
                                      keyscale=req.get("keyscale") or "",
                                      seed=seed, **extra)
            res = generate_music(dit, llm, params, GenerationConfig(batch_size=1, audio_format="wav", use_random_seed=False),
                                 save_dir=str(dst))
            paths = [a.get("path") for a in (res.audios or []) if a.get("path")]
            if not res.success or not paths:
                say(f"P {0.15 + 0.85 * k / total:.3f} кусок {part['name']}, сид {seed} не вышел: {getattr(res, 'error', '')}")
                continue
            say(f"W {part['name']} {seed} {paths[0]}")
    say("OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
