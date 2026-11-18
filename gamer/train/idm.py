"""Школа у ботов: обратная динамика (IDM) — по соседним кадрам понять, что игрок нажал между ними.

Идея друга (29.09): «боты в кс играют лучше неё — логику ботов догрузить в нейронки». Честно, без консоли и влезания
в сервер: Клава смотрит ботов наблюдателем «от первого лица» (запись — KlavaDesk, frames.csv без нажатий), а нажатия
бота восстанавливаются по картинке этой сетью — как у OpenAI VPT для Майнкрафта. Учится IDM на записях друга, где
кадры и настоящие нажатия есть; задача лёгкая — поворот мыши виден по сдвигу картинки, ход вперёд — по «наезду».
Размеченные записи ботов дальше идут в обычную учёбу ног (train.py) — это часы игры вместо минут.

    python idm.py train --game "Counter-Strike" [--epochs 8]
    python idm.py label --game "Counter-Strike"        # разметить все записи ботов (idm.txt в папке, нет actions.csv)

Вход: 4 соседних кадра (t-1, t, t+1, t+2) слоями каналов — ранняя склейка, чтобы первые свёртки видели разницу кадров;
выход: клавиши движения + ЛКМ и сдвиг мыши (классы MOUSE_BINS) между t и t+1.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import random
import sys
import time
from pathlib import Path

import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Dataset

from common import MOUSE_BINS, Row, bin_of, expected_move, load_jpeg, read_session, sessions_of

GAMER = Path(__file__).resolve().parents[1]
MOVE = ["W", "A", "S", "D", "SPACE", "CTRL", "SHIFT"]
OUTS = MOVE + ["LMB"]
SPAN = 4            # кадры t-1, t, t+1, t+2


class IDM(nn.Module):
    """ResNet-18 с первой свёрткой на 4 кадра сразу (веса ImageNet размножены и поделены) → клавиши, ЛКМ, мышь."""

    def __init__(self, pretrained: bool = True):
        super().__init__()
        import torchvision
        weights = torchvision.models.ResNet18_Weights.IMAGENET1K_V1 if pretrained else None
        net = torchvision.models.resnet18(weights=weights)
        conv = nn.Conv2d(3 * SPAN, 64, 7, 2, 3, bias=False)
        with torch.no_grad():
            conv.weight.copy_(net.conv1.weight.repeat(1, SPAN, 1, 1) / SPAN)
        net.conv1 = conv
        net.fc = nn.Identity()
        self.net = net
        self.head = nn.Sequential(nn.Linear(512, 384), nn.ReLU(inplace=True), nn.Dropout(0.2))
        self.keys = nn.Linear(384, len(OUTS))
        self.mx = nn.Linear(384, len(MOUSE_BINS))
        self.my = nn.Linear(384, len(MOUSE_BINS))

    def forward(self, x: torch.Tensor):
        # x: B x SPAN x 3 x H x W → B x (SPAN*3) x H x W
        h = self.head(self.net(x.flatten(1, 2)))
        return self.keys(h), self.mx(h), self.my(h)


def windows(rows: list[Row]) -> list[int]:
    """Индексы t, где кадры t-1..t+2 подряд в одном непрерывном куске."""
    out = []
    for t in range(1, len(rows) - 2):
        a, b = rows[t - 1], rows[t + 2]
        if a.seg == b.seg and b.frame - a.frame == SPAN - 1:
            out.append(t)
    return out


class Pairs(Dataset):
    def __init__(self, items: list[tuple[Path, list[Row], int]], augment: bool):
        self.items, self.augment = items, augment

    def __len__(self) -> int:
        return len(self.items)

    def __getitem__(self, i: int):
        folder, rows, t = self.items[i]
        x = torch.stack([load_jpeg(folder / f"f{rows[t + k].frame:07d}.jpg") for k in range(-1, SPAN - 1)])
        if self.augment:
            x = x * random.uniform(0.85, 1.15) + random.uniform(-0.15, 0.15)
        nxt = rows[t + 1]
        y = torch.tensor([1.0 if k in nxt.keys else 0.0 for k in MOVE] + [float(nxt.lmb)])
        return x, y, bin_of(nxt.dx), bin_of(nxt.dy)


def teacher_sessions(data: Path, game: str) -> list[Path]:
    """Записи с настоящими нажатиями: друг (и его показы в Арене); размеченные IDM записи ботов — не учитель."""
    return [f for f in sessions_of(data, game) if not (f / "idm.txt").exists() and not (f / "idm_labeled.txt").exists()]


def train(a: argparse.Namespace) -> int:
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    out = GAMER / "models" / a.game
    out.mkdir(parents=True, exist_ok=True)
    items_tr, items_va = [], []
    for folder in teacher_sessions(Path(a.data), a.game):
        rows = read_session(folder)
        idx = windows(rows)
        cut = int(len(idx) * 0.9)
        items_tr += [(folder, rows, t) for t in idx[:cut]]
        items_va += [(folder, rows, t) for t in idx[cut:]]
    if len(items_tr) < 200:
        print(f"мало записи друга для IDM: {len(items_tr)} примеров", file=sys.stderr)
        return 1
    print(f"IDM «{a.game}»: примеров {len(items_tr)}/{len(items_va)}, устройство {dev}", flush=True)
    counts = torch.zeros(len(OUTS))
    for folder, rows, t in items_tr[:: max(1, len(items_tr) // 20000)]:
        nxt = rows[t + 1]
        for j, k in enumerate(MOVE):
            counts[j] += k in nxt.keys
        counts[-1] += nxt.lmb
    n = max(1, len(items_tr[:: max(1, len(items_tr) // 20000)]))
    bce = nn.BCEWithLogitsLoss(pos_weight=((n - counts) / counts.clamp(min=1)).clamp(1, 20).to(dev))
    ce = nn.CrossEntropyLoss(label_smoothing=0.05)
    model = IDM(pretrained=True).to(dev)
    opt = torch.optim.AdamW(model.parameters(), lr=a.lr, weight_decay=1e-4)
    tl = DataLoader(Pairs(items_tr, True), batch_size=a.batch, shuffle=True, num_workers=a.workers,
                    persistent_workers=a.workers > 0, pin_memory=dev == "cuda", drop_last=True)
    vl = DataLoader(Pairs(items_va, False), batch_size=a.batch, shuffle=False, num_workers=a.workers,
                    persistent_workers=a.workers > 0)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=a.lr, total_steps=a.epochs * len(tl), pct_start=0.1)
    scaler = torch.amp.GradScaler(enabled=dev == "cuda")
    best = -1.0
    for epoch in range(1, a.epochs + 1):
        model.train()
        total = seen = 0
        for x, y, mx, my in tl:
            x, y, mx, my = x.to(dev, non_blocking=True), y.to(dev), mx.to(dev), my.to(dev)
            with torch.autocast(device_type=dev, enabled=dev == "cuda"):
                ky, lx, ly = model(x)
                loss = bce(ky.float(), y) + ce(lx.float(), mx) + ce(ly.float(), my)
            opt.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.step(opt)
            scaler.update()
            sched.step()
            total += float(loss.detach()) * x.shape[0]
            seen += x.shape[0]
        model.eval()
        probs, truth, mright, mn = [], [], 0, 0
        with torch.no_grad():
            for x, y, mx, my in vl:
                x, mx, my = x.to(dev), mx.to(dev), my.to(dev)
                with torch.autocast(device_type=dev, enabled=dev == "cuda"):
                    ky, lx, ly = model(x)
                probs.append(torch.sigmoid(ky.float()).cpu())
                truth.append(y)
                mright += int((lx.argmax(-1) - mx).abs().le(1).sum() + (ly.argmax(-1) - my).abs().le(1).sum())
                mn += 2 * mx.numel()
        pr, tr = torch.cat(probs), torch.cat(truth)
        f1s, ths = {}, {}
        for j, name in enumerate(OUTS):
            pos = tr[:, j] > 0.5
            if int(pos.sum()) == 0:
                continue
            bf, bt = 0.0, 0.5
            for th in [v / 20 for v in range(2, 18)]:
                pred = pr[:, j] >= th
                tp = int((pred & pos).sum()); fp = int((pred & ~pos).sum()); fn = int((~pred & pos).sum())
                f = 2 * tp / max(1, 2 * tp + fp + fn)
                if f > bf:
                    bf, bt = f, th
            f1s[name], ths[name] = round(bf, 3), bt
        mouse = mright / max(1, mn)
        key_f1 = sum(f1s.get(k, 0) for k in MOVE[:4]) / 4
        score = key_f1 + mouse
        print(f"эпоха {epoch}/{a.epochs}: ошибка {total / max(1, seen):.3f}, F1 {f1s}, мышь (±1 класс) {mouse:.3f}", flush=True)
        if score > best:
            best = score
            torch.save(model.state_dict(), out / "idm.pt.tmp")
            os.replace(out / "idm.pt.tmp", out / "idm.pt")
            (out / "idm_meta.json").write_text(json.dumps({"outs": OUTS, "thresholds": ths, "f1": f1s, "mouse_acc": round(mouse, 3),
                                                           "epoch": epoch, "bins": MOUSE_BINS}, ensure_ascii=False, indent=1),
                                               encoding="utf-8")
    print(f"готово: {out / 'idm.pt'}", flush=True)
    return 0


def label(a: argparse.Namespace) -> int:
    """Записи ботов (idm.txt, кадры + frames.csv) → actions.csv по предсказаниям IDM; idm.txt → idm_labeled.txt."""
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    mdir = GAMER / "models" / a.game
    meta = json.loads((mdir / "idm_meta.json").read_text(encoding="utf-8"))
    model = IDM(pretrained=False).to(dev)
    model.load_state_dict(torch.load(mdir / "idm.pt", map_location=dev, weights_only=True))
    model.eval()
    ths = meta.get("thresholds", {})
    base = Path(a.data) / a.game
    todo = sorted(p for p in base.iterdir() if (p / "idm.txt").exists() and (p / "frames.csv").exists()) if base.exists() else []
    if not todo:
        print("записей ботов для разметки нет")
        return 0
    for folder in todo:
        with open(folder / "frames.csv", encoding="utf-8", newline="") as f:
            frames = [(int(r["frame"]), int(r["t_ms"]), int(r["seg"])) for r in csv.DictReader(f)
                      if (folder / f"f{int(r['frame']):07d}.jpg").exists()]
        rows_out: dict[int, tuple] = {}
        t0 = time.time()
        batch_x, batch_i = [], []

        def flush() -> None:
            if not batch_x:
                return
            x = torch.stack(batch_x).to(dev)
            with torch.no_grad(), torch.autocast(device_type=dev, enabled=dev == "cuda"):
                ky, lx, ly = model(x)
            p = torch.sigmoid(ky.float()).cpu()
            for b, i in enumerate(batch_i):
                keys = [k for j, k in enumerate(MOVE) if float(p[b, j]) >= float(ths.get(k, 0.5))]
                lmb = float(p[b, len(MOVE)]) >= float(ths.get("LMB", 0.5))
                # метка кадра i+1 — что нажали между i и i+1 (как пишет GameRecorder)
                rows_out[i + 1] = ("+".join(keys), int(round(expected_move(lx[b]))), int(round(expected_move(ly[b]))), int(lmb))
            batch_x.clear()
            batch_i.clear()

        for i in range(1, len(frames) - 2):
            if frames[i - 1][2] != frames[i + 2][2] or frames[i + 2][0] - frames[i - 1][0] != SPAN - 1:
                continue
            batch_x.append(torch.stack([load_jpeg(folder / f"f{frames[i + k][0]:07d}.jpg") for k in range(-1, SPAN - 1)]))
            batch_i.append(i)
            if len(batch_x) == 64:
                flush()
        flush()
        with open(folder / "actions.csv", "w", encoding="utf-8", newline="") as f:
            f.write("frame,t_ms,seg,keys,dx,dy,lmb,rmb,mmb,wheel,cx,cy\n")
            for i, (fr, tms, seg) in enumerate(frames):
                keys, dx, dy, lmb = rows_out.get(i, ("", 0, 0, 0))
                # кадры без соседей (края кусков) — отдельным куском, чтобы учёба не склеила их с размеченными
                s = seg if i in rows_out else 100000 + i
                f.write(f"{fr},{tms},{s},{keys},{dx},{dy},{lmb},0,0,0,,\n")
        os.replace(folder / "idm.txt", folder / "idm_labeled.txt")
        walk = sum(1 for v in rows_out.values() if "W" in v[0]) / max(1, len(rows_out))
        print(f"{folder.name}: {len(rows_out)} кадров размечено за {time.time() - t0:.0f} с, W в {walk:.0%} кадров", flush=True)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=("train", "label"))
    ap.add_argument("--game", required=True)
    ap.add_argument("--data", default=str(GAMER / "data"))
    ap.add_argument("--epochs", type=int, default=8)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--workers", type=int, default=4)
    a = ap.parse_args()
    return train(a) if a.mode == "train" else label(a)


if __name__ == "__main__":
    sys.exit(main())
