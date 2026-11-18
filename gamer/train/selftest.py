"""Проверка учёбы без живой игры: синтетическая «игра» в формате записи KlavaDesk → обучение → ответы сервера.

    python selftest.py            (пишет gamer/data/klava_synthetic, учит gamer/models/klava_synthetic)

Сцена: серый фон 256x144, красный квадрат слева → друг жмёт A и ведёт мышь влево; справа → D и мышь вправо; квадрата
нет — ничего. Метка кадра — следующий ввод, как у настоящей записи. Та же сцена рисуется окном в KlavaDesk --gamer-test.
"""
from __future__ import annotations

import json
import random
import shutil
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw

GAMER = Path(__file__).resolve().parents[1]
GAME = "klava_synthetic"


def scene(kind: str) -> Image.Image:
    img = Image.new("RGB", (256, 144), (128, 128, 128))
    d = ImageDraw.Draw(img)
    if kind == "left":
        d.rectangle([32, 48, 80, 96], fill=(220, 30, 30))
    elif kind == "right":
        d.rectangle([176, 48, 224, 96], fill=(220, 30, 30))
    return img


def make_session(folder: Path, n: int) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    kinds, cur, left = [], "none", 0
    for _ in range(n):
        if left == 0:
            cur, left = random.choice(["left", "right", "none"]), random.randint(8, 30)
        kinds.append(cur)
        left -= 1
    rows = ["frame,t_ms,seg,keys,dx,dy,lmb,rmb,mmb,wheel,cx,cy"]
    for i, k in enumerate(kinds, start=1):
        img = scene(k)
        # чуть шума, как у живого кадра
        px = img.load()
        for _ in range(60):
            x, y = random.randrange(256), random.randrange(144)
            px[x, y] = tuple(max(0, min(255, c + random.randint(-25, 25))) for c in px[x, y])
        img.save(folder / f"f{i:07d}.jpg", quality=90)
        # ввод в строке i — то, что сделано ПОСЛЕ кадра i-1
        prev = kinds[i - 2] if i >= 2 else "none"
        keys, dx = {"left": ("A", -60), "right": ("D", 60)}.get(prev, ("", 0))
        rows.append(f"{i},{i * 62},0,{keys},{dx},0,0,0,0,0,0.5,0.5")
    (folder / "actions.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    (folder / "session.json").write_text(json.dumps({"game": GAME, "fps": 16}), encoding="utf-8")


def ask(port: int, kind: str) -> dict:
    import io
    buf = io.BytesIO()
    scene(kind).save(buf, format="JPEG", quality=90)
    data = buf.getvalue()
    with socket.create_connection(("127.0.0.1", port), timeout=30) as s:
        answer = {}
        for _ in range(7):   # набрать историю из одинаковых кадров
            s.sendall(struct.pack("<I", len(data)) + data)
            line = b""
            while not line.endswith(b"\n"):
                line += s.recv(4096)
            answer = json.loads(line)
        return answer


def main() -> int:
    data = GAMER / "data" / GAME
    shutil.rmtree(data, ignore_errors=True)
    random.seed(7)
    make_session(data / "20260101_000000", 1600)
    make_session(data / "20260101_010000", 400)
    py = sys.executable
    t = time.time()
    r = subprocess.run([py, "train.py", "--game", GAME, "--epochs", "3", "--batch", "32", "--workers", "2"],
                       cwd=Path(__file__).parent, text=True, capture_output=True, encoding="utf-8")
    print(r.stdout[-1500:], r.stderr[-1500:] if r.returncode else "", sep="\n")
    if r.returncode != 0:
        print("FAIL обучение упало")
        return 1
    status = json.loads((GAMER / "models" / GAME / "status.json").read_text(encoding="utf-8"))
    print(f"обучение: {time.time() - t:.0f} с, {status}")
    ok = status.get("state") == "done" and float(status.get("key_acc", 0)) > 0.9

    port = 47399
    srv = subprocess.Popen([py, "-u", "serve.py", "--model", str(GAMER / "models" / GAME), "--port", str(port)],
                           cwd=Path(__file__).parent, stdout=subprocess.PIPE, text=True, encoding="utf-8")
    try:
        line = srv.stdout.readline()
        print(line.strip())
        results = {k: ask(port, k) for k in ("left", "right", "none")}
    finally:
        srv.kill()
    for k, a in results.items():
        print(f"{k}: A={a['keys'].get('A')} D={a['keys'].get('D')} dx={a['dx']}")
    L, R, N = results["left"], results["right"], results["none"]
    checks = [
        (ok, "учёба сошлась (клавиши угаданы > 90%)"),
        (L["keys"].get("A", 0) > 0.5 and L["keys"].get("D", 1) < 0.5 and L["dx"] < -20, "квадрат слева → A и мышь влево"),
        (R["keys"].get("D", 0) > 0.5 and R["keys"].get("A", 1) < 0.5 and R["dx"] > 20, "квадрат справа → D и мышь вправо"),
        (N["keys"].get("A", 1) < 0.5 and N["keys"].get("D", 1) < 0.5 and abs(N["dx"]) < 20, "пусто → ничего не жмёт"),
    ]
    for good, what in checks:
        print(("OK   " if good else "FAIL ") + what)
    return 0 if all(g for g, _ in checks) else 1


if __name__ == "__main__":
    sys.exit(main())
