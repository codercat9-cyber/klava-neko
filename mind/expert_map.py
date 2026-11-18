"""Карта экспертов мозга Клавы: кто из 10 240 экспертов на чём срабатывает.

Через модель прогоняются настоящие реплики из её журнала (вопрос — ответ, считаются только слова ответа) и несколько
посторонних видов текста для сравнения; для каждого вида записывается, сколько слов прошло через каждого эксперта.
Из этого — «избирательные» эксперты: те, что на данном виде текста срабатывают заметно чаще, чем на остальных.
Это библиотека для обучения: учить потом не всю модель, а тех, кто отвечает за нужное (или за косяк).

  python mind\\expert_map.py [--device cpu|cuda] [--tokens 3000] [--bad-from 2026-10-01T06:00 --bad-to 2026-10-01T17:18]

Виды текста:
  болтовня — её ответы из журнала (всё, кроме «плохого» отрезка);
  шаблон   — её ответы за «плохой» отрезок (01.10 она полдня сидела на залипшем локальном мозге);
  люди     — слова людей из журнала (как текст, без роли);
  код      — исходники этой папки;
  вызовы   — вызовы инструментов из klava.log (JSON).
Итог: data\\mind\\expert_map.json.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from datetime import datetime
from pathlib import Path

import torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stand as S  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent


def journal_turns(journal: Path):
    """(время, слова людей, ответ Клавы) по всем дням журнала; только её ответы людям."""
    for file in sorted(journal.glob('????-??-??.jsonl')):
        for line in file.read_text(encoding='utf-8').splitlines():
            if not line.strip():
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            reply, user = (e.get('KlavaReply') or '').strip(), (e.get('UserText') or '').strip()
            if e.get('Kind') in ('turn', 'typed') and len(reply) > 20 and user and not user.startswith('('):
                yield datetime.fromisoformat(e['Timestamp'][:19]), user, reply


def tool_calls(log: Path, limit: int = 200):
    rx = re.compile(r'инструмент → (\w+) (\{.*)$')
    out = []
    for file in (log, log.with_name('klava.prev.log')):
        if file.exists():
            for line in file.read_text(encoding='utf-8', errors='replace').splitlines():
                m = rx.search(line)
                if m:
                    out.append(f'{{"name": "{m.group(1)}", "arguments": {m.group(2)[:400]}}}')
    return out[:limit]


def spread(items, n):
    """n штук равномерно по всему списку (а не первые n)."""
    if len(items) <= n:
        return items
    step = len(items) / n
    return [items[int(i * step)] for i in range(n)]


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', default=str(ROOT / 'models' / 'brain' / 'qwen36-base'))
    ap.add_argument('--device', default='cpu')
    ap.add_argument('--gpu-experts', type=int, default=800)
    ap.add_argument('--tokens', type=int, default=3000, help='сколько слов-токенов прогнать на каждый вид текста')
    ap.add_argument('--bad-from', default='2026-10-01T06:00')
    ap.add_argument('--bad-to', default='2026-10-01T17:18')
    ap.add_argument('--out', default=str(ROOT / 'data' / 'mind' / 'expert_map.json'))
    a = ap.parse_args()
    bad = (datetime.fromisoformat(a.bad_from), datetime.fromisoformat(a.bad_to))
    turns = list(journal_turns(ROOT / 'data' / 'journal'))
    good = [(u, r) for t, u, r in turns if not bad[0] <= t <= bad[1]]
    template = [(u, r) for t, u, r in turns if bad[0] <= t <= bad[1]]
    code = [p.read_text(encoding='utf-8')[i:i + 1500] for p in sorted(Path(__file__).resolve().parent.glob('*.py')) for i in (0, 3000)]
    print(f'журнал: ответов {len(turns)} (хороших {len(good)}, шаблонных {len(template)}); вызовов инструментов {len(tool_calls(ROOT / "data" / "logs" / "klava.log"))}')

    stand = S.build(a.base, a.device, a.gpu_experts)
    tok = stand.tokenizer

    def chat(user: str, reply: str):
        """Слова пары «вопрос — ответ» и отметка, какие из них — ответ."""
        head = tok.apply_chat_template([{'role': 'user', 'content': user}], tokenize=False, add_generation_prompt=True, enable_thinking=False)
        ids_head = tok(head, return_tensors='pt').input_ids
        ids = tok(head + reply, return_tensors='pt').input_ids[:, :384]
        mask = torch.zeros_like(ids, dtype=torch.bool)
        mask[:, ids_head.shape[1]:] = True
        return ids, mask

    def plain(text: str):
        ids = tok(text, return_tensors='pt').input_ids[:, :384]
        return ids, torch.ones_like(ids, dtype=torch.bool)

    kinds = {
        'болтовня': [chat(u, r) for u, r in spread(good, 400)],
        'шаблон': [chat(u, r) for u, r in spread(template, 400)],
        'люди': [plain(u) for u, _ in spread(good, 400)],
        'код': [plain(c) for c in code if c.strip()],
        'вызовы': [plain(c) for c in tool_calls(ROOT / 'data' / 'logs' / 'klava.log')],
    }
    result = {'layers': len(stand.blocks), 'experts': stand.blocks[0].num_experts, 'made': datetime.now().isoformat(timespec='seconds'), 'kinds': {}}
    usage = {}
    for kind, samples in kinds.items():
        if not samples:
            continue
        counted, t0 = 0, time.time()
        stand.record(False, reset=True)
        for ids, mask in samples:
            if counted >= a.tokens:
                break
            stand.record(True, mask=mask)
            with torch.no_grad():
                stand.model(ids.to(stand.device))
            counted += int(mask.sum())
        stand.record(False)
        usage[kind] = stand.usage().clone()
        result['kinds'][kind] = {'tokens': counted, 'usage': usage[kind].tolist()}
        print(f'  {kind}: {counted} слов за {time.time() - t0:.0f} с, разных экспертов на слой в среднем {float((usage[kind] > 0).sum(dim=1).float().mean()):.0f}', flush=True)

    # избирательность: доля срабатываний эксперта на этом виде против доли на всех остальных (со сглаживанием)
    result['selective'] = {}
    for kind, u in usage.items():
        others = sum(v for k, v in usage.items() if k != kind)
        p = (u + 1) / (u.sum(dim=1, keepdim=True) + u.shape[1])
        q = (others + 1) / (others.sum(dim=1, keepdim=True) + u.shape[1])
        lift = p / q
        score = lift * (u >= 5)
        top = torch.topk(score.flatten(), 200)
        result['selective'][kind] = [{'layer': int(i // u.shape[1]), 'expert': int(i % u.shape[1]), 'lift': round(float(lift.flatten()[i]), 2),
                                      'hits': int(u.flatten()[i])} for i in top.indices if float(score.flatten()[i]) > 1.5]
        print(f'  избирательных к «{kind}»: {len(result["selective"][kind])}, первые: '
              + ', '.join(f"{x['layer']}:{x['expert']}×{x['lift']}" for x in result['selective'][kind][:6]))
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(result, ensure_ascii=False), encoding='utf-8')
    print(f'карта: {out}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
