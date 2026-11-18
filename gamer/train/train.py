"""Обучить Клаву играть по записям друга.

    python train.py --game "Counter-Strike" [--epochs 8] [--batch 32] [--keys W,A,S,D,SPACE,CTRL,SHIFT] [--finetune]

--keys — учить только эти клавиши (шутеру ногам нужны клавиши движения; закуп B, «,» «.» — не ходьба: с ними 28.09
нейронка «стояла как истукан»). Показы друга посреди её игры (перехват в Арене, в папке записи есть dagger.txt) весят
вчетверо: это ровно те места, где она ошибалась. --finetune — дообучение поверх готовой модели (пара эпох, малый шаг):
так Арена учится динамически после каждого перехвата, сервер Арены подхватывает новые веса на лету.
Качество — F1 по каждой клавише с лучшим порогом (пороги пишутся в meta.json и берутся в игре), а не «точность»: у
редко нажатых клавиш «никогда не жать» и так даёт 90 %.

Записи — gamer/data/<игра>/<дата_время>/ (пишет KlavaDesk, инструмент gamer record_start). Модель — в
gamer/models/<игра>/policy.pt + meta.json, ход учёбы — в status.json (его читает Клава: «как там учёба?»).
Метка кадра t — что друг сделал ПОСЛЕ него (ввод между t и t+1): «видишь такое → делаешь так».
"""
from __future__ import annotations

import argparse
import json
import os
import random
import sys
import time
from pathlib import Path

import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Dataset, WeightedRandomSampler

from common import BUTTONS, STACK, STRIDE, Meta, Policy, Row, bin_of, load_jpeg, pick_keys, read_session, sessions_of

GAMER = Path(__file__).resolve().parents[1]


class Clips(Dataset):
    def __init__(self, items: list[tuple[Path, list[Row], int]], keys: list[str], augment: bool):
        self.items = items
        self.keys = keys
        self.augment = augment

    def __len__(self) -> int:
        return len(self.items)

    def __getitem__(self, i: int):
        folder, rows, t = self.items[i]
        frames = [load_jpeg(folder / f"f{rows[t - k * STRIDE].frame:07d}.jpg") for k in range(STACK - 1, -1, -1)]
        x = torch.stack(frames)
        if self.augment:
            # чуть светлее/темнее и контрастнее — разные карты и время суток; отражать нельзя: лево/право важны
            x = x * random.uniform(0.85, 1.15) + random.uniform(-0.15, 0.15)
        nxt = rows[t + 1]
        y = torch.zeros(len(self.keys) + len(BUTTONS))
        for j, k in enumerate(self.keys):
            if k in nxt.keys:
                y[j] = 1
        y[len(self.keys)] = float(nxt.lmb)
        y[len(self.keys) + 1] = float(nxt.rmb)
        return x, y, bin_of(nxt.dx), bin_of(nxt.dy)


DAGGER_WEIGHT = 4.0


def build(data_root: Path, game: str, only: list[str] | None = None):
    folders = sessions_of(data_root, game)
    if not folders:
        raise SystemExit(f"записей игры «{game}» нет в {data_root / game}")
    sessions = [(f, read_session(f)) for f in folders]
    keys = only or pick_keys([rows for _, rows in sessions])
    train, val = [], []
    for folder, rows in sessions:
        idx = []
        for t in range((STACK - 1) * STRIDE, len(rows) - 1):
            first = rows[t - (STACK - 1) * STRIDE]
            # вся история и следующий кадр — один непрерывный кусок, без пропусков (игра уходила с экрана)
            if first.seg == rows[t + 1].seg and rows[t + 1].frame - first.frame == (STACK - 1) * STRIDE + 1:
                idx.append((folder, rows, t))
        cut = int(len(idx) * 0.9)
        train += idx[:cut]
        val += idx[cut:]
    return keys, train, val, sum(len(r) for _, r in sessions)


def write_status(path: Path, **kw) -> None:
    kw["updated"] = time.strftime("%Y-%m-%d %H:%M:%S")
    path.write_text(json.dumps(kw, ensure_ascii=False), encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True)
    ap.add_argument("--data", default=str(GAMER / "data"))
    ap.add_argument("--out", default="")
    ap.add_argument("--epochs", type=int, default=8)
    ap.add_argument("--batch", type=int, default=32)
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--no-pretrained", action="store_true", help="без весов ImageNet (для проверки без сети)")
    ap.add_argument("--keys", default="", help="только эти клавиши через запятую (шутер: W,A,S,D,SPACE,CTRL,SHIFT)")
    ap.add_argument("--finetune", action="store_true", help="дообучить готовую модель (после перехвата в Арене)")
    a = ap.parse_args()
    if a.finetune:
        a.epochs = min(a.epochs, 2)
        a.lr = min(a.lr, 1e-4)

    out = Path(a.out) if a.out else GAMER / "models" / a.game
    out.mkdir(parents=True, exist_ok=True)
    status = out / "status.json"
    only = [k.strip().upper() for k in a.keys.split(",") if k.strip()] or None
    keys, train_items, val_items, frames = build(Path(a.data), a.game, only)
    if len(train_items) < 64:
        write_status(status, state="failed", error=f"мало записи: {len(train_items)} примеров — поиграй подольше")
        print(f"мало записи: {len(train_items)} примеров", file=sys.stderr)
        return 1
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"игра {a.game}: кадров {frames}, примеров {len(train_items)}/{len(val_items)}, клавиши {keys}, устройство {dev}", flush=True)

    # редкие клавиши весят больше, иначе нейронка выучит «никогда не жми R»
    counts = torch.zeros(len(keys) + len(BUTTONS))
    for _, rows, t in train_items[:: max(1, len(train_items) // 20000)]:
        nxt = rows[t + 1]
        for j, k in enumerate(keys):
            counts[j] += k in nxt.keys
        counts[len(keys)] += nxt.lmb
        counts[len(keys) + 1] += nxt.rmb
    n = max(1, len(train_items[:: max(1, len(train_items) // 20000)]))
    pos_weight = ((n - counts) / counts.clamp(min=1)).clamp(1, 20).to(dev)

    model = Policy(len(keys), pretrained=not a.no_pretrained and not a.finetune).to(dev)
    if a.finetune and (out / "policy.pt").exists() and (out / "meta.json").exists():
        old = Meta.load(out / "meta.json")
        if old.keys == keys:
            model.load_state_dict(torch.load(out / "policy.pt", map_location=dev, weights_only=True))
            print("дообучение поверх готовой модели", flush=True)
        else:
            print(f"клавиши модели {old.keys} ≠ {keys} — учу заново", flush=True)
            a.epochs = max(a.epochs, 6)
    enc = list(model.encoder.parameters())
    rest = [p for name, p in model.named_parameters() if not name.startswith("encoder.")]
    opt = torch.optim.AdamW([{"params": enc, "lr": a.lr / 3}, {"params": rest, "lr": a.lr}], weight_decay=1e-4)
    steps = a.epochs * max(1, len(train_items) // a.batch)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=[a.lr / 3, a.lr], total_steps=steps, pct_start=0.1)
    bce = nn.BCEWithLogitsLoss(pos_weight=pos_weight)
    ce = nn.CrossEntropyLoss(label_smoothing=0.05)
    scaler = torch.amp.GradScaler(enabled=dev == "cuda")
    # показы друга (перехват в Арене — dagger.txt в папке записи) вчетверо чаще: там она и ошибалась
    weights = [DAGGER_WEIGHT if (folder / "dagger.txt").exists() else 1.0 for folder, _, _ in train_items]
    dagger = sum(1 for w in weights if w > 1)
    if dagger:
        print(f"показов друга (перехват): {dagger} примеров, вес ×{DAGGER_WEIGHT:g}", flush=True)
    sampler = WeightedRandomSampler(weights, num_samples=len(train_items), replacement=True)
    tl = DataLoader(Clips(train_items, keys, True), batch_size=a.batch, sampler=sampler, num_workers=a.workers,
                    persistent_workers=a.workers > 0, pin_memory=dev == "cuda", drop_last=True)
    vl = DataLoader(Clips(val_items, keys, False), batch_size=a.batch, shuffle=False, num_workers=a.workers,
                    persistent_workers=a.workers > 0)
    meta = Meta(game=a.game, keys=keys, frames=frames)
    best = -1.0
    started = time.time()
    for epoch in range(1, a.epochs + 1):
        model.train()
        total, seen = 0.0, 0
        for i, (x, y, mx, my) in enumerate(tl):
            x, y, mx, my = x.to(dev, non_blocking=True), y.to(dev), mx.to(dev), my.to(dev)
            with torch.autocast(device_type=dev, enabled=dev == "cuda"):
                ky, lx, ly = model(x)
                loss = bce(ky.float(), y) + 0.5 * (ce(lx.float(), mx) + ce(ly.float(), my))
            opt.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.step(opt)
            scaler.update()
            sched.step()
            total += float(loss) * x.shape[0]
            seen += x.shape[0]
            if i % 50 == 0:
                done = (epoch - 1 + i / max(1, len(tl))) / a.epochs
                eta = (time.time() - started) / max(done, 1e-3) * (1 - done) / 60
                write_status(status, state="training", epoch=epoch, epochs=a.epochs, loss=round(total / max(1, seen), 4),
                             progress=round(done * 100, 1), eta_min=round(eta, 1), examples=len(train_items))
        # проверка на куске, которого нейронка не видела: угадала ли клавиши и куда мышь
        model.eval()
        right = cells = mouse_right = mouse_n = 0
        probs, truth = [], []
        with torch.no_grad():
            for x, y, mx, my in vl:
                x, y, mx, my = x.to(dev), y.to(dev), mx.to(dev), my.to(dev)
                with torch.autocast(device_type=dev, enabled=dev == "cuda"):
                    ky, lx, ly = model(x)
                right += int(((ky.float() > 0) == (y > 0.5)).sum())
                cells += y.numel()
                probs.append(torch.sigmoid(ky.float()).cpu())
                truth.append(y.cpu())
                mouse_right += int((lx.argmax(-1) - mx).abs().le(1).sum() + (ly.argmax(-1) - my).abs().le(1).sum())
                mouse_n += 2 * mx.numel()
        key_acc = right / max(1, cells)
        mouse_acc = mouse_right / max(1, mouse_n)
        # F1 и лучший порог по каждой клавише (кнопки мыши — тоже): честная мера, в отличие от «точности»
        pr, tr = torch.cat(probs), torch.cat(truth)
        f1s, thresholds = {}, {}
        for j, name in enumerate(keys + BUTTONS):
            best_f, best_t = 0.0, 0.5
            pos = tr[:, j] > 0.5
            if int(pos.sum()) == 0:
                continue
            for th in [x / 20 for x in range(2, 16)]:
                pred = pr[:, j] >= th
                tp = int((pred & pos).sum()); fp = int((pred & ~pos).sum()); fn = int((~pred & pos).sum())
                f = 2 * tp / max(1, 2 * tp + fp + fn)
                if f > best_f:
                    best_f, best_t = f, th
            f1s[name], thresholds[name] = round(best_f, 3), best_t
        move = [f1s[k] for k in keys if k in f1s]
        key_f1 = sum(move) / max(1, len(move))
        score = key_f1 + mouse_acc
        print(f"эпоха {epoch}/{a.epochs}: ошибка {total / max(1, seen):.4f}, клавиши F1 {key_f1:.3f} {f1s}, "
              f"мышь (±1 класс) {mouse_acc:.3f}", flush=True)
        if score > best:
            best = score
            meta.epochs = epoch
            meta.val_key_acc = round(key_f1, 4)
            meta.thresholds = thresholds
            # атомарно: сервер Арены подхватывает policy.pt на лету и не должен прочитать недописанный файл
            torch.save(model.state_dict(), out / "policy.pt.tmp")
            os.replace(out / "policy.pt.tmp", out / "policy.pt")
            meta.save(out / "meta.json")
        write_status(status, state="training", epoch=epoch, epochs=a.epochs, loss=round(total / max(1, seen), 4),
                     key_acc=round(key_f1, 4), mouse_acc=round(mouse_acc, 4), progress=round(epoch / a.epochs * 100, 1),
                     examples=len(train_items))
    write_status(status, state="done", epochs=a.epochs, best_epoch=meta.epochs, key_acc=meta.val_key_acc,
                 minutes=round((time.time() - started) / 60, 1), examples=len(train_items), keys=keys)
    print(f"готово: {out / 'policy.pt'}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
