"""Учёба Клавы играть по записям друга (клонирование поведения): общее для обучения и игры.

Кадры пишет KlavaDesk (GameRecorder): 256x144 JPEG, 16 кадров/с, рядом actions.csv —
frame,t_ms,seg,keys,dx,dy,lmb,rmb,mmb,wheel,cx,cy. Нейронка смотрит на 4 последних кадра (шаг 2 — это полсекунды
движения) и говорит, какие клавиши держать, куда повернуть мышь и стрелять ли.
"""
from __future__ import annotations

import csv
import io
import json
import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
from PIL import Image

W, H = 256, 144
STACK = 4          # сколько кадров видит нейронка
STRIDE = 2         # через сколько кадров берём: t, t-2, t-4, t-6
# движение мыши за кадр (сырые единицы Raw Input) — классы, как у сильных агентов CS: мелко около нуля, крупно по краям
MOUSE_BINS = [-600, -400, -250, -160, -100, -60, -32, -16, -8, -4, -2, 0, 2, 4, 8, 16, 32, 60, 100, 160, 250, 400, 600]
BUTTONS = ["LMB", "RMB"]
MAX_KEYS = 24
MEAN = torch.tensor([0.485, 0.456, 0.406]).view(3, 1, 1)
STD = torch.tensor([0.229, 0.224, 0.225]).view(3, 1, 1)


def bin_of(v: float) -> int:
    """Номер ближайшего класса движения мыши."""
    return int(np.argmin([abs(v - b) for b in MOUSE_BINS]))


def load_jpeg(data: bytes | Path) -> torch.Tensor:
    """JPEG → тензор 3xHxW, нормированный как для ImageNet. Один путь и для учёбы, и для игры."""
    img = Image.open(io.BytesIO(data) if isinstance(data, (bytes, bytearray)) else data).convert("RGB")
    if img.size != (W, H):
        img = img.resize((W, H), Image.BILINEAR)
    t = torch.from_numpy(np.asarray(img, dtype=np.uint8).copy()).permute(2, 0, 1).float() / 255.0
    return (t - MEAN) / STD


@dataclass
class Meta:
    """Что нейронка умеет жать: набор клавиш берётся из записей друга."""
    game: str
    keys: list[str]
    bins: list[int] = field(default_factory=lambda: list(MOUSE_BINS))
    stack: int = STACK
    stride: int = STRIDE
    frames: int = 0
    epochs: int = 0
    val_key_acc: float = 0.0
    thresholds: dict = field(default_factory=dict)   # лучший порог нажатия по каждой клавише (F1 на проверке)

    def save(self, path: Path) -> None:
        path.write_text(json.dumps(self.__dict__, ensure_ascii=False, indent=1), encoding="utf-8")

    @staticmethod
    def load(path: Path) -> "Meta":
        return Meta(**json.loads(path.read_text(encoding="utf-8")))


@dataclass
class Row:
    frame: int
    seg: int
    keys: set[str]
    dx: int
    dy: int
    lmb: bool
    rmb: bool


def read_session(folder: Path) -> list[Row]:
    rows: list[Row] = []
    with open(folder / "actions.csv", encoding="utf-8", newline="") as f:
        for r in csv.DictReader(f):
            try:
                rows.append(Row(int(r["frame"]), int(r["seg"]), set(k for k in r["keys"].split("+") if k),
                                int(r["dx"]), int(r["dy"]), r["lmb"] == "1", r["rmb"] == "1"))
            except (KeyError, ValueError):
                continue
    # кадр мог не записаться (оборвали запись) — строки без файла выкидываем
    return [r for r in rows if (folder / f"f{r.frame:07d}.jpg").exists()]


def sessions_of(data_root: Path, game: str) -> list[Path]:
    base = data_root / game
    return sorted(p for p in base.iterdir() if (p / "actions.csv").exists()) if base.exists() else []


def pick_keys(sessions: list[list[Row]]) -> list[str]:
    """Клавиши, которые друг реально жмёт (хотя бы в 0,2% кадров), самые частые — до MAX_KEYS. Esc/Tab/Alt не учим."""
    count: dict[str, int] = {}
    total = 0
    for rows in sessions:
        for r in rows:
            total += 1
            for k in r.keys:
                count[k] = count.get(k, 0) + 1
    skip = {"ESC", "TAB", "ALT", "ENTER", "BACKSPACE"}
    keys = [k for k, c in sorted(count.items(), key=lambda kv: -kv[1]) if k not in skip and c >= max(3, total * 0.002)]
    return keys[:MAX_KEYS]


class Policy(nn.Module):
    """ResNet-18 (обучена на ImageNet) смотрит на каждый из STACK кадров, признаки склеиваются → клавиши, кнопки, мышь."""

    def __init__(self, n_keys: int, pretrained: bool = True):
        super().__init__()
        import torchvision
        weights = torchvision.models.ResNet18_Weights.IMAGENET1K_V1 if pretrained else None
        net = torchvision.models.resnet18(weights=weights)
        self.encoder = nn.Sequential(*list(net.children())[:-1])   # → 512
        self.head = nn.Sequential(
            nn.Linear(512 * STACK, 768), nn.ReLU(inplace=True), nn.Dropout(0.3),
            nn.Linear(768, 384), nn.ReLU(inplace=True),
        )
        self.keys = nn.Linear(384, n_keys + len(BUTTONS))
        self.mx = nn.Linear(384, len(MOUSE_BINS))
        self.my = nn.Linear(384, len(MOUSE_BINS))

    def forward(self, x: torch.Tensor):
        # x: B x STACK x 3 x H x W
        b, s = x.shape[:2]
        f = self.encoder(x.flatten(0, 1)).flatten(1).view(b, s * 512)
        h = self.head(f)
        return self.keys(h), self.mx(h), self.my(h)


def expected_move(logits: torch.Tensor) -> float:
    """Движение мыши из классов: взвешенное среднее по вероятностям, но если «ноль» почти наверняка — ноль."""
    p = torch.softmax(logits.float(), dim=-1)
    zero = MOUSE_BINS.index(0)
    if float(p[zero]) > 0.6:
        return 0.0
    return float((p * torch.tensor(MOUSE_BINS, dtype=torch.float32, device=p.device)).sum())


def sigmoid(x: float) -> float:
    return 1.0 / (1.0 + math.exp(-x))
