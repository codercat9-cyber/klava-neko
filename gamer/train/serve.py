"""Нейронка-игрок: KlavaDesk шлёт кадр игры, в ответ — что жать.

    python serve.py --model gamer/models/<игра> [--port 47300]

Протокол (TCP, 127.0.0.1): клиент шлёт [длина u32 LE][JPEG кадра] — 16 раз в секунду; длина 0 — сброс истории (новая
игра/раунд). Ответ на каждый кадр — одна строка JSON: {"keys":{"W":0.93,...},"lmb":0.1,"rmb":0.0,"dx":12.5,"dy":-3.0}.
Клавиши и мышь жмёт сама KlavaDesk — лапками, с защитой (окно игры впереди, Esc, друг взялся за мышь).
"""
from __future__ import annotations

import argparse
import collections
import json
import socket
import struct
import sys
from pathlib import Path

import torch

from common import STACK, STRIDE, Meta, Policy, expected_move, load_jpeg, sigmoid


def recv_exact(conn: socket.socket, n: int) -> bytes | None:
    buf = bytearray()
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return bytes(buf)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--port", type=int, default=47300)
    a = ap.parse_args()
    folder = Path(a.model)
    meta = Meta.load(folder / "meta.json")
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    model = Policy(len(meta.keys), pretrained=False).to(dev)
    model.load_state_dict(torch.load(folder / "policy.pt", map_location=dev, weights_only=True))
    model.eval()
    need = (STACK - 1) * STRIDE + 1
    history: collections.deque[torch.Tensor] = collections.deque(maxlen=need)

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", a.port))
    srv.listen(1)
    print(f"READY {meta.game} клавиши={meta.keys} устройство={dev} порт={a.port}", flush=True)
    while True:
        conn, _ = srv.accept()
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        history.clear()
        with conn:
            while True:
                head = recv_exact(conn, 4)
                if head is None:
                    break
                (n,) = struct.unpack("<I", head)
                if n == 0:
                    history.clear()
                    conn.sendall(b"{}\n")
                    continue
                data = recv_exact(conn, n)
                if data is None:
                    break
                history.append(load_jpeg(data))
                while len(history) < need:
                    history.appendleft(history[0])
                frames = [history[-1 - k * STRIDE] for k in range(STACK - 1, -1, -1)]
                x = torch.stack(frames).unsqueeze(0).to(dev)
                with torch.no_grad(), torch.autocast(device_type=dev, enabled=dev == "cuda"):
                    ky, lx, ly = model(x)
                ky = ky[0].float().tolist()
                th = meta.thresholds or {}

                def cal(name: str, v: float) -> float:
                    """Вероятность, пересчитанная так, что лучший порог клавиши (F1 при учёбе) ложится на 0,5 — клиент
                    жмёт с 0,5 и отпускает с 0,35 для всех клавиш одинаково."""
                    p_, t_ = sigmoid(v), float(th.get(name, 0.5))
                    return 0.5 * p_ / t_ if p_ < t_ else 0.5 + 0.5 * (p_ - t_) / max(1e-6, 1 - t_)
                out = {
                    "keys": {k: round(cal(k, ky[i]), 3) for i, k in enumerate(meta.keys)},
                    "lmb": round(cal("LMB", ky[len(meta.keys)]), 3),
                    "rmb": round(cal("RMB", ky[len(meta.keys) + 1]), 3),
                    "dx": round(expected_move(lx[0]), 1),
                    "dy": round(expected_move(ly[0]), 1),
                }
                conn.sendall((json.dumps(out) + "\n").encode("utf-8"))


if __name__ == "__main__":
    sys.exit(main())
