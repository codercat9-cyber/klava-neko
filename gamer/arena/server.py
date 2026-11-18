"""Арена — боевой сервер Клавы для шутеров (CS 1.6 с ботами, Serious Sam и прочие): видит только картинку игры,
отвечает, что жать на клаве и как вести мышь. Честно: ни памяти игры, ни консоли, ни инъекций — пиксели на вход,
клавиши и мышь на выход, и мышь — по-человечески (реакция, предел скорости поворота, дрожь, очереди).

    python server.py --game "Counter-Strike" [--model gamer/models/<игра>] [--port 47310]

Протокол (TCP 127.0.0.1): клиент (KlavaDesk, ArenaPlayer) шлёт кадр ~30 раз в секунду:
    [u32 LE длина JSON][u32 длина JPEG всего кадра 640][u32 длина JPEG середины][u32 длина JPEG 256x144] + байты
  JSON: {"t": мс, "user": true|false — друг сейчас сам держит клаву/мышь (учит), "event": "kill"|"death"|"round"|null,
         "hold": true — стоять на месте (только целиться), "team": "T"|"CT"|"" — её команда,
         "tag": "friend"|"enemy"|null — что игра пишет про человека под прицелом («Товарищ : имя» / «Враг : имя», OCR),
         "dead": true — её убили, до раунда ничего не жать (на экране чужая камера), "menu": true — на экране меню игры}
  весь кадр и середина (центральная половина по ширине, 30–80 % высоты, в родном разрешении, сжатая до 640) — для
  глаз: дальние цели у горизонта крошечные, на 640 от всего кадра их не видно; маленький — для выученной нейронки
  движения (ровно как в записях).
Ответ — строка JSON: {"keys": ["W", ...] — что держать зажатым, "dx": .., "dy": .. — мышь (сырые единицы),
  "lmb": 0|1, "rmb": 0|1, "yield": true — друг играет сам, ничего не жать, "why": "...", "targets": n, "gain": ..}
Длина JSON 0 — сброс (новый раунд/карта): история и цель забываются.

Глаза — YOLO11n-pose (gamer/models/detect/yolo11n-pose.pt): люди и точки лица → голова. Ноги — нейронка из записей
друга (gamer train, та же Policy), если есть; нет — разведчик: вперёд, осмотр, выход из тупиков. Свои и чужие:
цвет формы (в сборке друга террористы в красном, спецназ в синем — suit), надпись игры под прицелом (tag) и выученные
по надписям цвета одежды (Teams). По своим не стреляет; неизвестного сначала держит на прицеле до ярлыка.
"""
from __future__ import annotations

import argparse
import collections
import json
import math
import random
import socket
import struct
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np
import torch

ARENA = Path(__file__).resolve().parent
GAMER = ARENA.parent
sys.path.insert(0, str(GAMER / "train"))
from common import STACK, STRIDE, Meta, Policy, expected_move, load_jpeg, sigmoid  # noqa: E402

# ---------------------------------------------------------------- глаза


@dataclass
class Foe:
    """Человек на кадре (координаты большого кадра, px)."""
    x0: float
    y0: float
    x1: float
    y1: float
    conf: float
    head: tuple[float, float]
    head_known: bool

    @property
    def w(self) -> float:
        return self.x1 - self.x0

    @property
    def h(self) -> float:
        return self.y1 - self.y0

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2


class Eyes:
    """
    YOLO11n-pose: люди + ключевые точки (0 нос, 1-2 глаза, 3-4 уши) → точка головы. Сеть крутится CUDA-графом:
    замер 28.09 — обычный predict 27–38 мс (сотни мелких запусков ядер, карта в P8 на 405 МГц даже не просыпается),
    граф — 3 мс. Разбор выхода свой: [1, 56, N] = рамка xywh, уверенность, 17 точек × (x, y, видимость).
    """

    def __init__(self, weights: Path, dev: str):
        from ultralytics import YOLO
        self.dev = dev
        self.net = YOLO(str(weights)).model.to(dev).eval()
        self.half = dev == "cuda"
        if self.half:
            self.net = self.net.half()
        self.graphs: dict[tuple[int, int], tuple] = {}

    def _runner(self, h: int, w: int, n: int = 1):
        """Граф под размер входа (высота кратна 32) и число кадров n (батч): вход, выход, граф (или None на CPU)."""
        key = (h, w, n)
        if key in self.graphs:
            return self.graphs[key]
        dtype = torch.half if self.half else torch.float32
        inp = torch.full((n, 3, h, w), 114 / 255, device=self.dev, dtype=dtype)
        with torch.no_grad():
            if self.dev != "cuda":
                self.graphs[key] = (inp, None, None)
                return self.graphs[key]
            s = torch.cuda.Stream()
            s.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(s):
                for _ in range(3):
                    self.net(inp)
            torch.cuda.current_stream().wait_stream(s)
            g = torch.cuda.CUDAGraph()
            with torch.cuda.graph(g):
                out = self.net(inp)[0]
        self.graphs[key] = (inp, out, g)
        return self.graphs[key]

    def look(self, bgr: np.ndarray, conf: float, own_hands: bool = True) -> list[Foe]:
        """Люди на кадре (координаты этого кадра). own_hands — отсекать свои руки с оружием внизу по центру."""
        return self.look_many([bgr], conf, [own_hands])[0]

    def look_many(self, imgs: list[np.ndarray], conf: float, own_hands: list[bool]) -> list[list[Foe]]:
        """
        Несколько кадров одного размера — одним прогоном сети (батч, свой граф): весь кадр и середина вместе — ~8 мс
        вместо 2 × 7 (замер 28.09); и одна пересылка на видеокарту, одна выгрузка результата.
        """
        import torchvision
        H, W = imgs[0].shape[:2]
        ph = (H + 31) // 32 * 32
        pw = (W + 31) // 32 * 32
        inp, out, g = self._runner(ph, pw, len(imgs))
        top = (ph - H) // 2
        # BGR как есть — каналы переворачиваются уже на видеокарте: разворот на процессоре (отрицательный шаг) стоил 3 мс
        batch = torch.from_numpy(np.stack(imgs)).to(self.dev, non_blocking=True)
        t = batch.permute(0, 3, 1, 2).flip(1).to(inp.dtype) / 255
        with torch.no_grad():
            inp.fill_(114 / 255)
            inp[:, :, top:top + H, :W] = t
            if g is not None:
                g.replay()
                preds = out
            else:
                preds = self.net(inp)[0]
            # 40 самых уверенных на кадр — на видеокарте и ОДНОЙ выгрузкой: отбор по маске и NMS там же ждали
            # видеокарту на каждом шаге (4 мс на разбор, замер 28.09)
            k = min(40, preds.shape[2])
            ids = preds[:, 4, :].topk(k, dim=1).indices
            top_k = torch.gather(preds, 2, ids.unsqueeze(1).expand(-1, preds.shape[1], -1)).float().cpu().numpy()
        cpu = []
        for b in range(len(imgs)):
            a = top_k[b].T                                                  # [k, 56]
            a = a[a[:, 4] > conf]
            if a.shape[0] == 0:
                cpu.append(None)
                continue
            xy, wh = a[:, :2], a[:, 2:4]
            boxes = np.concatenate([xy - wh / 2, xy + wh / 2], 1)
            keep = torchvision.ops.nms(torch.from_numpy(boxes), torch.from_numpy(a[:, 4].copy()), 0.5)[:12].numpy()
            cpu.append((boxes[keep], a[keep, 4], a[keep, 5:].reshape(-1, 17, 3)))
        res: list[list[Foe]] = []
        for b, x in enumerate(cpu):
            foes: list[Foe] = []
            if x is not None:
                boxes, confs, kps = x
                for i, (x0, y0, x1, y1) in enumerate(boxes):
                    y0, y1 = y0 - top, y1 - top
                    w, h = x1 - x0, y1 - y0
                    # руки с оружием самой Клавы внизу по центру и мелочь меньше 6 px — не люди на карте
                    if own_hands[b] and y1 > H * 0.97 and abs((x0 + x1) / 2 - W / 2) < W * 0.25 and h > H * 0.35:
                        continue
                    if h < 6 or w < 3:
                        continue
                    head, known = (float((x0 + x1) / 2), float(y0 + 0.12 * h)), False
                    face = [(kx, ky - top) for kx, ky, kc in kps[i][:5] if kc > 0.45]
                    if len(face) >= 2:
                        head = (float(np.mean([q[0] for q in face])), float(np.mean([q[1] for q in face])))
                        known = True
                    foes.append(Foe(float(x0), float(y0), float(x1), float(y1), float(confs[i]), head, known))
            res.append(foes)
        return res

    def look2(self, full: np.ndarray, center: np.ndarray | None, conf: float) -> list[Foe]:
        """
        Два прохода: весь кадр (640) и его середина в родном разрешении (центральная половина по ширине, с 30 % до
        80 % по высоте — туда смотрит прицел и там горизонт с дальними целями). Середина переводится в координаты
        всего кадра, одинаковые люди из двух проходов склеиваются.
        """
        if center is None or center.shape != full.shape:
            foes = self.look(full, conf)
            if center is None:
                return foes
            mids = self.look(center, conf, own_hands=False)
        else:
            foes, mids = self.look_many([full, center], conf, [True, False])
        H, W = full.shape[:2]
        ch, cw = center.shape[:2]
        # середина = прямоугольник [W/4, 3W/4] × [0,3H, 0,8H] всего кадра
        sx, sy = (W / 2) / cw, (0.5 * H) / ch
        ox, oy = W / 4, 0.3 * H
        for f in mids:
            m = Foe(ox + f.x0 * sx, oy + f.y0 * sy, ox + f.x1 * sx, oy + f.y1 * sy, f.conf,
                    (ox + f.head[0] * sx, oy + f.head[1] * sy), f.head_known)
            if not any(abs(o.cx - m.cx) < max(6, 0.6 * o.w) and abs(o.y0 - m.y0) < max(6, 0.4 * o.h) for o in foes):
                foes.append(m)
        # своё оружие и рука внизу справа: второй проход (середина кадра) их не отсекал — прицел садился на свой пистолет
        # (и AK-47 в полкадра: рамка на стволе кончалась на 84 % высоты — теперь по середине рамки в правом нижнем углу)
        return [f for f in foes if not ((f.y1 > 0.9 * H and f.head[1] > 0.6 * H and f.cx > 0.52 * W)
                                        or (f.cx > 0.55 * W and (f.y0 + f.y1) / 2 > 0.68 * H and f.w < 0.4 * W))]


# ---------------------------------------------------------------- свои и чужие


class Teams:
    """
    Свои и чужие — честно, по картинке. Ярлык даёт сама игра: когда прицел на человеке, CS пишет внизу «Товарищ : имя»
    или «Враг : имя» (KlavaDesk читает строку OCR и шлёт tag). С ярлыком запоминается цвет одежды этого человека
    (гистограмма оттенка и насыщенности торса); дальше новые люди сравниваются с запомненными (3 ближайших) — у
    своих Клава даже не задерживает прицел. Подтверждённый товарищ ещё и «помечен» на экране на пару секунд.
    """

    def __init__(self) -> None:
        self.samples: list[tuple[np.ndarray, bool]] = []
        self.marks: list[list[float]] = []          # [x, y, h, до] — где сейчас помеченные свои

    @staticmethod
    def feat(bgr: np.ndarray, f: Foe) -> np.ndarray | None:
        H, W = bgr.shape[:2]
        x0 = int(max(0, f.x0 + 0.2 * f.w)); x1 = int(min(W, f.x1 - 0.2 * f.w))
        y0 = int(max(0, f.y0 + 0.2 * f.h)); y1 = int(min(H, f.y0 + 0.7 * f.h))
        if x1 - x0 < 3 or y1 - y0 < 3:
            return None
        hsv = cv2.cvtColor(bgr[y0:y1, x0:x1], cv2.COLOR_BGR2HSV)
        h = cv2.calcHist([hsv], [0, 1], None, [12, 4], [0, 180, 0, 256]).flatten()
        return h / max(1.0, float(h.sum()))

    def learn(self, bgr: np.ndarray, f: Foe, friend: bool, now: float) -> None:
        v = self.feat(bgr, f)
        if v is not None:
            self.samples.append((v, friend))
            if len(self.samples) > 400:
                self.samples.pop(0)
        if friend:
            self.marks.append([f.cx, f.head[1], f.h, now + 2.5])

    def friendish(self, bgr: np.ndarray, f: Foe) -> float | None:
        """Похож ли на своих: 0..1; None — примеров ещё мало (нужно хотя бы по 2 своих и чужих)."""
        if sum(1 for _, s in self.samples if s) < 2 or sum(1 for _, s in self.samples if not s) < 2:
            return None
        v = self.feat(bgr, f)
        if v is None:
            return None
        d = sorted((float(np.abs(v - s).sum()), fr) for s, fr in self.samples)[:3]
        return sum(1.0 for _, fr in d if fr) / len(d)

    def marked(self, f: Foe, now: float) -> bool:
        self.marks = [m for m in self.marks if m[3] > now]
        for m in self.marks:
            if abs(m[0] - f.cx) < max(20.0, 0.8 * f.w) and abs(m[1] - f.head[1]) < max(20.0, 0.5 * f.h):
                m[0], m[1], m[2] = f.cx, f.head[1], f.h           # помеченный свой ходит — метка за ним
                return True
        return False


def suit(bgr: np.ndarray, f: Foe) -> str | None:
    """
    Цвет формы на торсе: в сборке друга террористы в красном, спецназ в синем (кадры 28.09: «Товарищ» в красном рядом
    с ней-террористом, спецназовец со щитом — синий). None — не разобрать: далеко, в тени, или стандартные модели.
    Песок и камень de_dust — оттенок 10–30 при средней насыщенности, ни в красное, ни в синее не попадают.
    """
    H, W = bgr.shape[:2]
    # всё тело без головы: со спины торс закрыт серым жилетом — красные тогда руки и ноги (кадр 045518)
    x0 = int(max(0, f.x0 + 0.1 * f.w)); x1 = int(min(W, f.x1 - 0.1 * f.w))
    y0 = int(max(0, f.y0 + 0.15 * f.h)); y1 = int(min(H, f.y1 - 0.05 * f.h))
    if x1 - x0 < 2 or y1 - y0 < 3:
        return None
    hsv = cv2.cvtColor(bgr[y0:y1, x0:x1], cv2.COLOR_BGR2HSV)
    h, s, v = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    sat = (s > 60) & (v > 25)             # в тени красный темнеет до V ~30 (кадр 045518)
    red = float(np.mean(sat & ((h < 8) | (h > 170))))
    blue = float(np.mean(sat & (h > 98) & (h < 128)))
    if red > 0.06 and red > 2.5 * blue:
        return "T"
    if blue > 0.06 and blue > 2.5 * red:
        return "CT"
    return None


def under_cross(foes: list[Foe], cx: float, cy: float) -> Foe | None:
    """Человек под прицелом — про него игра и пишет «Товарищ/Враг»: рамка с прицелом внутри, иначе ближняя голова."""
    inside = [f for f in foes if f.x0 <= cx <= f.x1 and f.y0 <= cy <= f.y1]
    if inside:
        return max(inside, key=lambda f: f.h)
    near = [f for f in foes if math.hypot(f.head[0] - cx, f.head[1] - cy) < max(30.0, 0.8 * f.h)]
    return min(near, key=lambda f: math.hypot(f.head[0] - cx, f.head[1] - cy)) if near else None


# ---------------------------------------------------------------- мышь как у человека


@dataclass
class Aim:
    """
    Прицел: цель держится с запасом (не прыгает между людьми), реакция на нового врага 160–260 мс, поворот не быстрее
    предела (px/с по экрану), к цели — плавно с перелётом не больше пары пикселей, мелкая дрожь руки. Огонь —
    очередями, пока прицел на теле; между очередями пауза (отдача спадает). Во время очереди — лёгкая тяга вниз.
    """
    gain: float = 1.0                 # единиц мыши на пиксель у прицела (калибровка)
    target: Foe | None = None
    seen_at: float = 0.0              # когда эту цель впервые увидела
    react: float = 0.2
    lost_at: float = 0.0
    burst_until: float = 0.0
    rest_until: float = 0.0
    shots: int = 0
    max_px_s: float = 3000.0          # предел скорости поворота по экрану, px/с (640-кадр); 1500 не успевал за бегущим
    pitch: float = 0.0                # сколько единиц мыши по вертикали ушло с начала раунда (на респе взгляд ровный)
    # доворот прошлого ответа (px кадра): клиент снимает следующий кадр, пока сервер думает, — в этом кадре его ещё
    # нет; без учёта сервер доворачивал ту же ошибку второй раз, прицел перелетал и плясал вокруг цели (28.09: 0 фрагов)
    pend_x: float = 0.0
    pend_y: float = 0.0
    # упреждение: скорость головы цели (px/с, сглаженная) — бегущую цель прицел догонял на 20–40 px позади (29.09)
    vx: float = 0.0
    vy: float = 0.0
    last_head: tuple[float, float, float] | None = None
    lead: float = 0.12                # с: задержка кадр→ответ→мышь→кадр игры
    tap: bool = False                 # огонь тапами: нажал кадр — отпустил кадр

    def pick(self, foes: list[Foe], cx: float, cy: float, now: float) -> Foe | None:
        if not foes:
            # глаза моргнули (кадр без людей) — прежнюю цель держим 0,35 с, дальше забываем
            if self.target is None:
                return None
            if self.lost_at == 0.0:
                self.lost_at = now
            if now - self.lost_at > 0.35:
                self.target = None
            return self.target
        self.lost_at = 0.0

        def cost(f: Foe) -> float:
            # ближе к прицелу и крупнее (ближе к ней — опаснее) — первым
            return math.hypot(f.head[0] - cx, f.head[1] - cy) - 2.5 * f.h

        best = min(foes, key=cost)
        if self.target is not None:
            # та же цель, если рядом с прошлой (гистерезис): иначе прицел мечется между двумя людьми
            same = min(foes, key=lambda f: math.hypot(f.head[0] - self.target.head[0], f.head[1] - self.target.head[1]))
            if math.hypot(same.head[0] - self.target.head[0], same.head[1] - self.target.head[1]) < max(40.0, 1.5 * self.target.h):
                self.target = same
                return same
        self.target = best
        self.seen_at = now
        # вблизи (крупная цель) — быстрее: бот рядом стреляет сразу, «реагирую» 0,2 с стоило ей жизни (29.09)
        self.react = random.uniform(0.11, 0.18) if best.h > 90 else random.uniform(0.16, 0.26)
        self.vx = self.vy = 0.0
        self.last_head = None
        return best

    def step(self, foe: Foe | None, cx: float, cy: float, now: float, dt: float,
             hold_fire: bool = False) -> tuple[float, float, bool, str]:
        """→ (dx, dy в единицах мыши, огонь, почему). hold_fire — вести прицел, но не стрелять (ещё не ясно, свой ли)."""
        if foe is None:
            self.shots = 0
            self.pend_x = self.pend_y = 0.0
            return 0.0, 0.0, False, "цели нет"
        if now - self.seen_at < self.react:
            self.pend_x = self.pend_y = 0.0
            return 0.0, 0.0, False, "увидела — реагирую"
        # точка: голова, если лицо видно; иначе верх корпуса (голова по рамке угадывается хуже)
        tx, ty = foe.head if foe.head_known else (foe.cx, foe.y0 + 0.2 * foe.h)
        # скорость цели по экрану: движение головы между кадрами плюс свой доворот (он тоже двигает картинку)
        if self.last_head is not None and now > self.last_head[2]:
            gap = now - self.last_head[2]
            if gap < 0.25:
                svx = (tx - self.last_head[0] + self.pend_x) / gap
                svy = (ty - self.last_head[1] + self.pend_y) / gap
                self.vx = 0.6 * self.vx + 0.4 * max(-900.0, min(900.0, svx))
                self.vy = 0.6 * self.vy + 0.4 * max(-400.0, min(400.0, svy))
        self.last_head = (tx, ty, now)
        tx += self.vx * self.lead
        ty += self.vy * self.lead * 0.5
        ex, ey = tx - cx - self.pend_x, ty - cy - self.pend_y
        dist = math.hypot(ex, ey)
        k = 0.7 if dist > 25 else 0.8                         # издалека — рывок, у цели — доводка (отложенный доворот учтён)
        mx, my = ex * k, ey * k
        cap = self.max_px_s * dt
        n = math.hypot(mx, my)
        if n > cap:
            mx, my = mx * cap / n, my * cap / n
        mx += random.gauss(0, 0.6)                             # дрожь руки
        my += random.gauss(0, 0.6)
        # на теле ли прицел: по ширине рамки, по высоте — от макушки до пояса
        on_body = foe.x0 + 0.2 * foe.w <= cx <= foe.x1 - 0.2 * foe.w and foe.y0 <= cy <= foe.y0 + 0.55 * foe.h
        fire = False
        why = f"веду к цели ({dist:.0f} px)"
        if on_body and dist < max(6.0, 0.6 * foe.w):
            if hold_fire:
                why = "на прицеле — жду надпись: свой или чужой"
            elif now < self.burst_until:
                fire, why = True, "очередь"
            elif now >= self.rest_until:
                # дальняя цель (маленькая) — короткие очереди/одиночные, близкая — длиннее
                length = 0.09 if foe.h < 40 else 0.18 if foe.h < 110 else 0.3
                self.burst_until = now + length
                self.rest_until = self.burst_until + (0.22 if foe.h < 40 else 0.14)
                self.shots += 1
                fire, why = True, "огонь"
            else:
                why = "пауза между очередями"
        if fire:
            # тапами: пистолет от зажатой кнопки стреляет один раз, а автомат тапами кучнее очереди
            self.tap = not self.tap
            fire = self.tap
            my += 1.4 * dt * 60                               # тяга вниз против отдачи
        self.pend_x, self.pend_y = mx, my
        return mx * self.gain, my * self.gain, fire, why


def calibrate_shift(before: np.ndarray, after: np.ndarray) -> float | None:
    """Сдвиг картинки по X в центре кадра (фазовая корреляция), px; None — не уверена."""
    h, w = before.shape[:2]
    a = cv2.cvtColor(before[h // 4: 3 * h // 4, w // 4: 3 * w // 4], cv2.COLOR_BGR2GRAY).astype(np.float32)
    b = cv2.cvtColor(after[h // 4: 3 * h // 4, w // 4: 3 * w // 4], cv2.COLOR_BGR2GRAY).astype(np.float32)
    win = cv2.createHanningWindow(a.shape[::-1], cv2.CV_32F)
    (sx, sy), resp = cv2.phaseCorrelate(a, b, win)
    if resp < 0.08 or abs(sy) > abs(sx) + 3:
        return None
    return float(sx)


# ---------------------------------------------------------------- ноги


MOVE_KEYS = {"W", "A", "S", "D", "SPACE", "CTRL", "SHIFT"}


class Legs:
    """Движение: выученная нейронка друга (если есть), иначе разведчик; нейронка 1,5 с не ходит — ведёт разведчик."""

    def __init__(self, model_dir: Path | None, dev: str):
        self.dev = dev
        self.policy = None
        self.meta: Meta | None = None
        need = (STACK - 1) * STRIDE + 1
        self.history: collections.deque[torch.Tensor] = collections.deque(maxlen=need)
        self.model_dir = model_dir
        self.loaded_at = 0.0          # mtime policy.pt, которую держим
        self.checked_at = 0.0
        self.reload()
        self.turn_until = 0.0
        self.turn_dx = 0.0
        self.strafe = "A"
        self.strafe_until = 0.0
        self.still_since = 0.0
        self.prev_small: np.ndarray | None = None
        self.jump_at = 0.0
        self.idle_since = 0.0            # с какого момента выученная нейронка не жмёт движение
        self.last: dict | None = None      # прошлое решение нейронки (считается через кадр)
        self.thumbs: collections.deque[tuple[float, np.ndarray]] = collections.deque()   # середина кадра за 0,6 с

    def reload(self) -> bool:
        """
        Взять policy.pt, если она новее той, что держим: динамическое дообучение (train.py --finetune после перехвата
        другом) пишет новую модель атомарно — ноги умнеют прямо посреди игры, без перезапуска сервера.
        """
        d = self.model_dir
        if d is None or not (d / "policy.pt").exists() or not (d / "meta.json").exists():
            return False
        mtime = (d / "policy.pt").stat().st_mtime
        if mtime <= self.loaded_at:
            return False
        try:
            meta = Meta.load(d / "meta.json")
            policy = Policy(len(meta.keys), pretrained=False).to(self.dev)
            policy.load_state_dict(torch.load(d / "policy.pt", map_location=self.dev, weights_only=True))
        except (RuntimeError, OSError, ValueError, KeyError) as ex:
            print(f"ноги: новую модель не взяла ({type(ex).__name__}: {ex}) — играю на прежней", flush=True)
            return False
        policy.eval()
        first = self.policy is None
        self.meta, self.policy, self.loaded_at = meta, policy, mtime
        if not first:
            print(f"ноги: подхватила дообученную модель (эпоха {meta.epochs}, F1 клавиш {meta.val_key_acc})", flush=True)
        return True

    def maybe_reload(self, now: float) -> None:
        if now - self.checked_at > 3.0:
            self.checked_at = now
            self.reload()

    def reset(self) -> None:
        self.history.clear()
        self.prev_small = None
        self.still_since = 0.0
        self.idle_since = 0.0
        self.last = None
        self.thumbs.clear()

    def push(self, small_jpeg: bytes) -> None:
        """Кадр в историю без прогона нейронки (кадр между её решениями) — чтобы шаг по времени не ломался."""
        if self.policy is not None:
            self.history.append(load_jpeg(small_jpeg))

    def learned(self, small_jpeg: bytes) -> dict | None:
        if self.policy is None or self.meta is None:
            return None
        self.history.append(load_jpeg(small_jpeg))
        while len(self.history) < self.history.maxlen:
            self.history.appendleft(self.history[0])
        frames = [self.history[-1 - k * STRIDE] for k in range(STACK - 1, -1, -1)]
        x = torch.stack(frames).unsqueeze(0).to(self.dev)
        with torch.no_grad(), torch.autocast(device_type=self.dev, enabled=self.dev == "cuda"):
            ky, lx, ly = self.policy(x)
        ky = ky[0].float().tolist()
        # ногам — только движение, и с порогом 0,3: на 11 минутах записи W почти никогда не переходила 0,5 («стояла
        # как истукан», 28.09); B, «,» и «.» — закуп, это не ходьба (закупает клиент по началу раунда)
        th = self.meta.thresholds or {}
        keys = [k for i, k in enumerate(self.meta.keys) if k in MOVE_KEYS and sigmoid(ky[i]) >= float(th.get(k, 0.3))]
        return {"keys": keys, "dx": expected_move(lx[0]), "dy": expected_move(ly[0]),
                "lmb": sigmoid(ky[len(self.meta.keys)]) >= 0.5}

    def blocked(self, small: np.ndarray, now: float, walking: bool) -> bool:
        """
        Упёрлась: жмёт W, а середина кадра (без оружия внизу справа и без HUD) за полсекунды почти не изменилась
        ~0,8 с подряд. По всему кадру покачивание ствола при ходьбе в стену выглядело как движение — «тыкалась в стены»
        (друг, 28.09).
        """
        g = cv2.resize(cv2.cvtColor(small, cv2.COLOR_BGR2GRAY), (64, 36)).astype(np.float32)[4:24, 8:52]
        self.thumbs.append((now, g))
        while self.thumbs and now - self.thumbs[0][0] > 0.6:
            self.thumbs.popleft()
        old = self.thumbs[0]
        moving = now - old[0] < 0.3 or float(np.mean(np.abs(g - old[1]))) > 3.0
        if not walking or moving:
            self.still_since = 0.0
            return False
        if self.still_since == 0.0:
            self.still_since = now
        return now - self.still_since > 0.8

    def unstick(self, now: float) -> tuple[list[str], float]:
        """Разворот на 60–110° с прыжком (порог или ящик перепрыгнет, в стену — уйдёт вбок)."""
        self.still_since = 0.0
        self.thumbs.clear()
        self.turn_until = now + random.uniform(0.35, 0.6)
        # px кадра 640 за кадр: ~0,14° на px, 60–90 px × 6–10 кадров ≈ 60–110° (прежние 25–45 — едва 40°, снова в стену)
        self.turn_dx = random.choice([-1, 1]) * random.uniform(60, 90)
        self.jump_at = now
        return ["SPACE"], self.turn_dx

    def scout(self, small: np.ndarray, now: float, fighting: bool) -> tuple[list[str], float, str]:
        """Разведчик без выученной нейронки: вперёд с осмотром; упёрлась — разворот и прыжок."""
        if fighting:
            # в бою: стрейф из стороны в сторону (в неё труднее попасть), но огонь — только стоя (см. main)
            if now > self.strafe_until:
                self.strafe = "D" if self.strafe == "A" else "A"
                self.strafe_until = now + random.uniform(0.35, 0.7)
            return [self.strafe], 0.0, "стрейф в бою"
        if now < self.turn_until:
            return [], self.turn_dx, "разворот из тупика"
        if self.blocked(small, now, walking=True):
            keys, dx = self.unstick(now)
            return keys, dx, "упёрлась — разворот и прыжок"
        # осмотр на ходу: медленная «восьмёрка» взглядом
        look = math.sin(now * 0.7) * 3.0
        return ["W"], look, "иду вперёд"


# ---------------------------------------------------------------- сервер


def recv_exact(conn: socket.socket, n: int) -> bytes | None:
    buf = bytearray()
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return bytes(buf)


def decode(jpeg: bytes) -> np.ndarray:
    return cv2.imdecode(np.frombuffer(jpeg, np.uint8), cv2.IMREAD_COLOR)


@dataclass
class Stats:
    frames: int = 0
    seen: int = 0
    shots: int = 0
    kills: int = 0
    deaths: int = 0
    taught: int = 0
    t0: float = field(default_factory=time.time)


class Debug:
    """
    --debug папка: 3 кадра в секунду (и каждый кадр с выстрелом) — большой кадр с разметкой: люди (красный — враг,
    зелёный — свой, жёлтый — неясно), цель и точка прицела, крестик, почему стреляет или нет. Хранит последние 600.
    """

    COL = {"enemy": (0, 0, 255), "friend": (0, 200, 0), "unsure": (0, 220, 255)}

    def __init__(self, folder: str):
        self.dir = Path(folder) if folder else None
        self.n = 0
        if self.dir:
            self.dir.mkdir(parents=True, exist_ok=True)
            for old in self.dir.glob("*.jpg"):
                old.unlink()

    def frame(self, bgr: np.ndarray, marks: list[tuple[Foe, str, str | None]], target: Foe | None, cx: float, cy: float,
              text: str, fire: bool, every: int) -> None:
        if not self.dir or (not fire and every % 5):
            return
        img = cv2.resize(bgr, (bgr.shape[1] * 2, bgr.shape[0] * 2))
        k = 2
        for f, cls, s in marks:
            c = self.COL[cls]
            cv2.rectangle(img, (int(f.x0 * k), int(f.y0 * k)), (int(f.x1 * k), int(f.y1 * k)), c, 2)
            cv2.circle(img, (int(f.head[0] * k), int(f.head[1] * k)), 4, c, -1 if f.head_known else 1)
            cv2.putText(img, f"{cls[:2]} {s or '-'} {f.conf:.2f} h{f.h:.0f}", (int(f.x0 * k), int(f.y0 * k) - 4),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.45, c, 1)
        if target is not None:
            cv2.circle(img, (int(target.head[0] * k), int(target.head[1] * k)), 10, (255, 0, 255), 2)
        cv2.drawMarker(img, (int(cx * k), int(cy * k)), (255, 255, 255), cv2.MARKER_CROSS, 18, 1)
        cv2.putText(img, ("FIRE " if fire else "") + text, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
        self.n += 1
        cv2.imwrite(str(self.dir / f"{self.n % 600:04d}.jpg"), img, [cv2.IMWRITE_JPEG_QUALITY, 80])


def serve(a: argparse.Namespace) -> int:
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    eyes = Eyes(Path(a.detector), dev)
    legs = Legs(Path(a.model) if a.model else None, dev)
    calib_file = ARENA / "calib.json"
    calib = json.loads(calib_file.read_text(encoding="utf-8")) if calib_file.exists() else {}
    aim = Aim(gain=float(calib.get(a.game, 0) or 0) or 1.0)
    need_calib = a.game not in calib
    teams = Teams()
    dbg = Debug(a.debug)
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", a.port))
    srv.listen(1)
    print(f"READY арена «{a.game}» глаза={Path(a.detector).name} ноги={'нейронка ' + legs.meta.game if legs.meta else 'разведчик'} "
          f"мышь={'калибровка' if need_calib else f'{aim.gain:.2f} ед/px'} устройство={dev} порт={a.port}", flush=True)
    while True:
        conn, _ = srv.accept()
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        st = Stats()
        legs.reset()
        aim.target = None
        teams.marks.clear()
        # калибровка: шаги (единиц мыши вправо), кадр до и после каждого
        steps = [160, -160, 160, -160] if need_calib else []
        calib_prev: np.ndarray | None = None
        calib_wait = 0
        measured: list[float] = []
        last = time.time()
        with conn:
            while True:
                # --proto 3: [json][кадр][маленький] (клиент ArenaPlayer от 28.09 утра), 4: + середина кадра
                head = recv_exact(conn, 16 if a.proto == 4 else 12)
                if head is None:
                    break
                if a.proto == 4:
                    jl, bl, cl, sl = struct.unpack("<IIII", head)
                else:
                    (jl, bl, sl), cl = struct.unpack("<III", head), 0
                if jl == 0:
                    legs.reset(); aim.target = None; aim.pitch = 0.0
                    conn.sendall(b"{}\n")
                    continue
                meta = json.loads(recv_exact(conn, jl) or b"{}")
                big = recv_exact(conn, bl) if bl else None
                mid = recv_exact(conn, cl) if cl else b""
                small = recv_exact(conn, sl) if sl else None
                if big is None or small is None:
                    break
                now = time.time()
                dt = min(0.1, max(0.005, now - last))
                last = now
                st.frames += 1
                ev = meta.get("event")
                if ev == "kill":
                    st.kills += 1
                elif ev == "death":
                    st.deaths += 1
                    aim.target = None
                if ev in ("death", "round"):
                    legs.reset()
                    aim.pitch = 0.0
                bgr = decode(big)
                H, W = bgr.shape[:2]
                cx, cy = W / 2, H / 2
                out: dict = {"keys": [], "dx": 0.0, "dy": 0.0, "lmb": 0, "rmb": 0}

                if meta.get("dead") or meta.get("menu"):
                    # убили — на экране камера смерти или чужой игрок; меню — не игра: ничего не жмём, цель забываем
                    aim.target = None
                    legs.reset()
                    out.update({"yield": True, "why": "меня убили — жду раунда" if meta.get("dead") else "на экране меню"})
                elif meta.get("user"):
                    # друг играет сам — учит: ничего не жмём (его ввод пишет KlavaDesk в записи для учёбы)
                    st.taught += 1
                    aim.pitch = 0.0                # взгляд водит друг — после него считаем ровным от его положения
                    legs.learned(small)            # история кадров не рвётся — после передачи играть сразу
                    out.update({"yield": True, "why": "друг играет сам — смотрю и учусь"})
                elif steps or calib_wait:
                    # калибровка мыши: сдвиг → через 3 кадра смотрим, на сколько уехала картинка
                    if calib_wait == 0:
                        calib_prev = bgr
                        out["dx"] = float(steps[0])
                        calib_wait = 4
                        out["why"] = "калибрую мышь"
                    else:
                        calib_wait -= 1
                        out["why"] = "калибрую мышь"
                        if calib_wait == 0:
                            shift = calibrate_shift(calib_prev, bgr) if calib_prev is not None else None
                            if shift is not None and abs(shift) > 4:
                                measured.append(abs(steps[0]) / abs(shift))
                            steps.pop(0)
                            if not steps:
                                if measured:
                                    aim.gain = float(np.median(measured))
                                    calib[a.game] = round(aim.gain, 4)
                                    calib_file.write_text(json.dumps(calib, ensure_ascii=False, indent=1), encoding="utf-8")
                                    print(f"калибровка «{a.game}»: {aim.gain:.3f} ед/px по {len(measured)} замерам", flush=True)
                                else:
                                    print("калибровка не вышла — картинка не сдвигалась (меню? стена?) — беру 1.0", flush=True)
                                need_calib = False
                else:
                    foes = eyes.look2(bgr, decode(mid) if mid else None, a.conf)
                    if foes:
                        st.seen += 1
                    my_team = str(meta.get("team") or "")
                    enemy_team = {"T": "CT", "CT": "T"}.get(my_team, "")
                    # надпись игры про человека под прицелом — урок для различения по одежде; буквы не прочитались —
                    # хватает её цвета (красная — террорист, синяя — спецназ)
                    tag = meta.get("tag")
                    if tag is None and my_team and meta.get("label_team") in ("T", "CT"):
                        tag = "friend" if meta["label_team"] == my_team else "enemy"
                    tagged = under_cross(foes, cx, cy) if tag in ("friend", "enemy") else None
                    if tagged is not None:
                        teams.learn(bgr, tagged, tag == "friend", now)
                    targets: list[Foe] = []
                    unsure: set[int] = set()
                    marks: list[tuple[Foe, str, str | None]] = []
                    for f in foes:
                        s = suit(bgr, f) if a.suits and my_team else None
                        fr = teams.friendish(bgr, f)
                        if f is tagged:
                            if tag == "friend":
                                marks.append((f, "friend", s))
                                continue
                        elif s == my_team or teams.marked(f, now) or (fr is not None and fr > 0.66):
                            marks.append((f, "friend", s))
                            continue                                    # свой — не цель
                        elif not (s == enemy_team or (fr is not None and fr < 0.34)):
                            unsure.add(id(f))                           # не ясно — вести, но стрелять после ярлыка
                        marks.append((f, "unsure" if id(f) in unsure else "enemy", s))
                        targets.append(f)
                    if tagged is not None and tag == "friend" and aim.target is not None and \
                            math.hypot(aim.target.head[0] - tagged.head[0], aim.target.head[1] - tagged.head[1]) < max(20.0, 0.5 * tagged.h):
                        aim.target = None                               # вела своего — бросить
                    foe = aim.pick(targets, cx, cy, now)
                    # неясного держим на прицеле до надписи «Враг», но не дольше 0,6 с сверх реакции: надписи нет — чужой;
                    # своя команда ещё неизвестна — только по надписи. Огонь — только по тому, кто на этом кадре: по месту,
                    # где цель была 0,35 с назад, очереди уходили в стену (отладочные кадры 29.09)
                    visible = foe is not None and any(f is foe for f in targets)
                    hold = foe is not None and (not visible or (id(foe) in unsure and (not my_team or now - aim.seen_at < aim.react + 0.6)))
                    mdx, mdy, fire, why = aim.step(foe, cx, cy, now, dt, hold_fire=hold)
                    fighting = foe is not None
                    # выученные ноги — через кадр (15 раз в секунду хватает ходьбе, нейронка ~15 мс); между — прошлое решение
                    if st.frames % 2 == 0 or legs.last is None:
                        legs.last = legs.learned(small)
                    else:
                        legs.push(small)
                    learned = legs.last
                    small_bgr = decode(small)
                    if learned is not None and learned["keys"]:
                        legs.idle_since = 0.0
                    elif learned is not None and legs.idle_since == 0.0:
                        legs.idle_since = now
                    if learned is not None and not fighting and now < legs.turn_until:
                        keys, mdx, why = [], legs.turn_dx * aim.gain, "разворот из тупика"
                    elif learned is not None and (learned["keys"] or now - legs.idle_since < 1.5):
                        keys = learned["keys"]
                        if not fighting:
                            mdx, mdy = learned["dx"], learned["dy"]
                            why = "иду, как учил друг"
                            if legs.blocked(small_bgr, now, walking="W" in keys):
                                keys, turn = legs.unstick(now)
                                mdx, why = turn * aim.gain, "упёрлась — разворот и прыжок"
                    else:
                        keys, look, lwhy = legs.scout(small_bgr, now, fighting)
                        if not fighting:
                            mdx, why = look * aim.gain, lwhy
                    if fighting:
                        # увидела врага — встаёт и бьёт: в CS на бегу пули летят веером, а стрейф против бегущей цели
                        # уводил прицел — 29.09 выученные ноги стрейфились вправо, пока цель бежала влево, и прицел
                        # полсекунды висел в 150 px от неё. Вблизи — присесть (кучнее)
                        keys = [k for k in keys if k not in ("W", "A", "S", "D", "SPACE")]
                        if foe is not None and foe.h > 120 and "CTRL" not in keys:
                            keys.append("CTRL")
                    if not fighting:
                        # поводок взгляда: вне боя вертикаль возвращается к уровню респа — выученная мышь понемногу
                        # уводила взгляд в пол (друг, 28.09: «то в пол, то в стены»)
                        mdy = max(-6.0, min(6.0, mdy)) - 0.12 * aim.pitch
                    if meta.get("hold"):
                        keys = [k for k in keys if k not in ("W", "A", "S", "D", "SPACE")]
                    if fire:
                        st.shots += 1
                    dbg.frame(bgr, marks, foe, cx, cy, f"{why} | dx {mdx:.0f} dy {mdy:.0f} | T={my_team} tag={tag} keys={'+'.join(keys)}",
                              fire, st.frames)
                    aim.pitch += mdy
                    legs.maybe_reload(now)
                    out.update({"keys": keys, "dx": round(mdx, 1), "dy": round(mdy, 1), "lmb": int(fire),
                                "why": why, "targets": len(foes)})
                out["gain"] = round(aim.gain, 3)
                if st.frames % 300 == 0:
                    el = max(1.0, time.time() - st.t0)
                    print(f"кадров {st.frames} ({st.frames / el:.0f}/с), с врагом {st.seen}, очередей {st.shots}, "
                          f"убила {st.kills}, умерла {st.deaths}, учил друг {st.taught} кадров", flush=True)
                conn.sendall((json.dumps(out, ensure_ascii=False) + "\n").encode("utf-8"))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True)
    ap.add_argument("--model", default="", help="папка выученной нейронки движения (gamer/models/<игра>)")
    ap.add_argument("--detector", default=str(GAMER / "models" / "detect" / "yolo11n-pose.pt"))
    ap.add_argument("--conf", type=float, default=0.35)
    ap.add_argument("--port", type=int, default=47310)
    ap.add_argument("--proto", type=int, default=4, choices=(3, 4), help="3 — без середины кадра, 4 — с ней")
    ap.add_argument("--debug", default="", help="папка для кадров с разметкой (что видит и почему стреляет)")
    ap.add_argument("--no-suits", dest="suits", action="store_false",
                    help="не различать команды по цвету формы (сборка со стандартными моделями)")
    return serve(ap.parse_args())


if __name__ == "__main__":
    sys.exit(main())
