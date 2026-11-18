"""Проверка NitroGen: поднять сервер с ng.pt, отправить три кадра, замерить, сколько модель думает на кадр.

    python nitro_check.py
"""
from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

import numpy as np

GAMER = Path(__file__).resolve().parents[1]
NITRO = GAMER / "nitrogen"
PORT = 5599


def main() -> int:
    sys.path.insert(0, str(NITRO))
    from nitrogen.inference_client import ModelClient

    srv = subprocess.Popen([sys.executable, "-u", str(NITRO / "scripts" / "serve.py"), str(GAMER / "models" / "ng.pt"), "--port", str(PORT)],
                           cwd=NITRO, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    t0 = time.time()
    tail: list[str] = []
    try:
        up = False
        for line in srv.stdout:
            tail = (tail + [line.rstrip()])[-12:]
            if "Waiting for requests" in line:
                up = True
                break
            if time.time() - t0 > 300:
                break
        if not up:
            print("FAIL сервер NitroGen не поднялся:\n" + "\n".join("  " + t[:200] for t in tail))
            return 1
        print(f"загрузка модели: {time.time() - t0:.0f} с")
        client = ModelClient(host="127.0.0.1", port=PORT)
        img = (np.random.default_rng(1).random((256, 256, 3)) * 255).astype(np.uint8)
        times = []
        pred = None
        for _ in range(3):
            t = time.time()
            pred = client.predict(img)
            times.append(time.time() - t)
        print(f"ответ на кадр: {', '.join(f'{x * 1000:.0f} мс' for x in times)}")
        print(f"действий за ответ: {len(pred['buttons'])}, пример: левый стик {np.round(pred['j_left'][0], 2).tolist()}, кнопок {len(pred['buttons'][0])}")
        print("OK   NitroGen грузится и отвечает")
        return 0
    finally:
        srv.kill()


if __name__ == "__main__":
    sys.exit(main())
