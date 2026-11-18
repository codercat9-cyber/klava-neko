"""Первый запуск мозга на стенде: поднять из несжатых весов, спросить, замерить.

  python mind\\probe.py [--device cpu|cuda] [--gpu-experts 800] [--ask "вопрос"] [--tokens 40]

Показывает: сколько заняла общая часть (ОЗУ / видеопамять), ответ модели, скорость, сколько экспертов подкачано с диска
и сколько разных экспертов сработало в каждом слое (из 256).
"""
from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

import torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stand as S  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent


def ram_gb() -> float:
    try:
        import psutil
        return psutil.Process().memory_info().rss / 2**30
    except ImportError:
        return float('nan')


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', default=str(ROOT / 'models' / 'brain' / 'qwen36-base'))
    ap.add_argument('--device', default='cpu')
    ap.add_argument('--gpu-experts', type=int, default=800)
    ap.add_argument('--ask', default='Привет! Скажи одной фразой по-русски, кто ты и что умеешь.')
    ap.add_argument('--tokens', type=int, default=40)
    ap.add_argument('--threads', type=int, default=8)
    a = ap.parse_args()
    torch.set_num_threads(a.threads)
    t0 = time.time()
    stand = S.build(a.base, a.device, a.gpu_experts)
    shared = sum(p.numel() for p in stand.model.parameters())
    print(f'поднято за {time.time() - t0:.0f} с: общая часть {shared / 1e9:.2f} млрд параметров, ОЗУ процесса {ram_gb():.1f} ГБ'
          + (f', видеопамять {torch.cuda.memory_allocated() / 2**30:.1f} ГБ' if a.device != 'cpu' else ''))
    stand.record(True, reset=True)
    t1 = time.time()
    reply = stand.say([{'role': 'user', 'content': a.ask}], max_new_tokens=a.tokens)
    dt = time.time() - t1
    n = len(stand.tokenizer(reply).input_ids)
    print(f'вопрос: {a.ask}\nответ: {reply}')
    used = stand.usage()
    hit = (used > 0).sum(dim=1)
    print(f'{n} слов-токенов за {dt:.1f} с ({n / dt:.2f} в секунду); подкачек экспертов с диска {stand.cache.loads}, из кэша {stand.cache.hits}')
    print(f'разных экспертов на слой: от {int(hit.min())} до {int(hit.max())} из {used.shape[1]}, в среднем {float(hit.float().mean()):.0f}; '
          f'ОЗУ процесса {ram_gb():.1f} ГБ')
    return 0


if __name__ == '__main__':
    sys.exit(main())
