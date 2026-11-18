"""Стенд мозга Клавы: Qwen3.6-35B-A3B, разложенный по железу одного домашнего компьютера.

Друг 01.10.2026: «весами модели мы можем оперировать как хотим: буфер под обучение на NVMe, часть на процессоре с
оперативой, часть на видяхе; взять слои и правильно распределить, поработать с экспертами и завести библиотеку обучения
самих экспертов — заставить весь комп целиком пахать на подобие ИИ».

Устройство модели (замер 01.10): 40 слоёв, в каждом 256 экспертов по ~3 млн параметров, на слово работают 8 из них;
эксперты — 32 из 34,7 млрд параметров (93 %), общая часть (внимание, маршрутизаторы, словарь) — ~2,4 млрд. Поэтому:
  - общая часть лежит в памяти целиком (видеокарта или процессор);
  - банк экспертов остаётся на NVMe в исходных файлах safetensors (отображение файла в память, без копии в ОЗУ);
  - сработавшие эксперты подкачиваются на устройство и держатся там в очереди «кто давно не нужен — уходит»;
  - эксперты, которые учатся, — отдельные настоящие параметры поверх банка: их можно править, сохранять, откатывать.
Заодно каждый слой считает, какие эксперты на каких словах срабатывают, — из этого строится карта экспертов.

Запуск проверки на крошечной копии архитектуры (без весов и без видеокарты): python mind\\stand.py --selfcheck
"""
from __future__ import annotations

import collections
import json
import struct
import sys
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from torch import nn
from transformers.activations import ACT2FN

LANG = 'model.language_model.'


# ---------------------------------------------------------------- банк экспертов на диске

class DiskTensor:
    """Тензор внутри файла safetensors, отображённый в память: срез читает с диска только свои байты."""

    DTYPES = {'BF16': (np.uint16, torch.bfloat16), 'F16': (np.float16, torch.float16), 'F32': (np.float32, torch.float32)}

    def __init__(self, file: Path, name: str):
        with open(file, 'rb') as f:
            n = struct.unpack('<Q', f.read(8))[0]
            meta = json.loads(f.read(n))[name]
        np_type, self.dtype = self.DTYPES[meta['dtype']]
        self.shape = tuple(meta['shape'])
        self.map = np.memmap(file, dtype=np_type, mode='r', offset=8 + n + meta['data_offsets'][0], shape=self.shape)

    def __getitem__(self, idx: int) -> torch.Tensor:
        return torch.from_numpy(np.ascontiguousarray(self.map[idx])).view(self.dtype)


class RamTensor:
    """То же для весов, уже лежащих в памяти (крошечная модель в проверке, эксперты из своего хранилища)."""

    def __init__(self, tensor: torch.Tensor):
        self.t, self.dtype, self.shape = tensor.detach(), tensor.dtype, tuple(tensor.shape)

    def __getitem__(self, idx: int) -> torch.Tensor:
        return self.t[idx]


class ExpertCache:
    """Сработавшие эксперты на устройстве: общий на все слои запас в `budget` штук, давно не нужные уходят первыми."""

    def __init__(self, budget: int):
        self.budget = budget
        self.items: collections.OrderedDict[tuple[int, int], tuple[torch.Tensor, torch.Tensor]] = collections.OrderedDict()
        self.loads = self.hits = 0

    def get(self, key, load):
        got = self.items.get(key)
        if got is not None:
            self.items.move_to_end(key)
            self.hits += 1
            return got
        got = load()
        self.loads += 1
        self.items[key] = got
        while len(self.items) > self.budget:
            self.items.popitem(last=False)
        return got

    def drop(self, key):
        self.items.pop(key, None)


class TieredExperts(nn.Module):
    """Эксперты одного слоя: замена библиотечного блока, в котором все 256 лежат одним тензором в памяти.

    Замороженные берутся из банка (диск → устройство через общий кэш), обучаемые — из `trained` (параметры float32).
    Счёт тот же, что в библиотеке: по каждому сработавшему эксперту — его слова, gate·up → down, вес маршрутизатора.
    """

    def __init__(self, layer: int, config, gate_up, down, cache: ExpertCache, dtype: torch.dtype):
        super().__init__()
        self.layer, self.num_experts = layer, config.num_experts
        self.gate_up, self.down, self.cache, self.dtype = gate_up, down, cache, dtype
        self.act_fn = ACT2FN[config.hidden_act]
        self.trained = nn.ParameterDict()
        self.record = False
        self.mask: torch.Tensor | None = None   # какие слова считать (по порядку в пачке); None — все
        self.usage = torch.zeros(self.num_experts, dtype=torch.long)       # сколько слов прошло через эксперта
        self.weight_sum = torch.zeros(self.num_experts, dtype=torch.float64)  # и с каким суммарным весом

    def frozen(self, e: int, device) -> tuple[torch.Tensor, torch.Tensor]:
        return self.cache.get((self.layer, e), lambda: (self.gate_up[e].to(device=device, dtype=self.dtype),
                                                        self.down[e].to(device=device, dtype=self.dtype)))

    def unfreeze(self, e: int, device) -> None:
        """Сделать эксперта обучаемым: копия его весов из банка становится параметром (float32 — учить в bf16 грубо)."""
        if str(e) in self.trained:
            return
        self.trained[str(e)] = nn.ParameterList([nn.Parameter(self.gate_up[e].to(device=device, dtype=torch.float32)),
                                                 nn.Parameter(self.down[e].to(device=device, dtype=torch.float32))])
        self.cache.drop((self.layer, e))

    def forward(self, hidden_states: torch.Tensor, top_k_index: torch.Tensor, top_k_weights: torch.Tensor) -> torch.Tensor:
        out = torch.zeros_like(hidden_states)
        if self.record:
            idx, w = top_k_index.cpu(), top_k_weights.double().cpu()
            if self.mask is not None:
                idx, w = idx[self.mask], w[self.mask]
            flat = idx.reshape(-1)
            self.usage += torch.bincount(flat, minlength=self.num_experts)
            self.weight_sum += torch.zeros(self.num_experts, dtype=torch.float64).index_add_(0, flat, w.reshape(-1))
        for e in torch.unique(top_k_index).tolist():
            token_idx, k_pos = torch.where(top_k_index == e)
            own = self.trained[str(e)] if str(e) in self.trained else None
            gu, dn = (own[0], own[1]) if own is not None else self.frozen(e, hidden_states.device)
            x = hidden_states[token_idx].to(gu.dtype)
            gate, up = F.linear(x, gu).chunk(2, dim=-1)
            y = F.linear(self.act_fn(gate) * up, dn)
            y = y * top_k_weights[token_idx, k_pos, None].to(y.dtype)
            out.index_add_(0, token_idx, y.to(out.dtype))
        return out


# ---------------------------------------------------------------- сборка

def tier(model, cache: ExpertCache, sources, dtype: torch.dtype):
    """Подменить в каждом слое блок экспертов на ярусный. sources(layer) → (gate_up, down) — откуда брать веса."""
    blocks = []
    for i, layer in enumerate(model.model.layers):
        gu, dn = sources(i)
        layer.mlp.experts = TieredExperts(i, model.config, gu, dn, cache, dtype)
        blocks.append(layer.mlp.experts)
    return blocks


class Stand:
    """Собранный мозг: модель, словарь, ярусные блоки экспертов и общий кэш."""

    def __init__(self, model, tokenizer, blocks, cache, device):
        self.model, self.tokenizer, self.blocks, self.cache, self.device = model, tokenizer, blocks, cache, device

    def record(self, on: bool = True, reset: bool = False, mask: torch.Tensor | None = None):
        """Считать срабатывания экспертов. mask — булев вектор по словам пачки (по порядку): считать только их
        (например, только слова её ответа, без вопроса)."""
        for b in self.blocks:
            b.record = on
            b.mask = mask.reshape(-1).cpu() if mask is not None else None
            if reset:
                b.usage.zero_()
                b.weight_sum.zero_()

    def usage(self) -> torch.Tensor:
        """[слой, эксперт] — сколько слов прошло через каждого эксперта с момента сброса."""
        return torch.stack([b.usage for b in self.blocks])

    @torch.no_grad()
    def say(self, messages, max_new_tokens: int = 60, **gen) -> str:
        text = self.tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True, enable_thinking=False)
        ids = self.tokenizer(text, return_tensors='pt').input_ids.to(self.device)
        out = self.model.generate(ids, max_new_tokens=max_new_tokens, do_sample=False, **gen)
        return self.tokenizer.decode(out[0, ids.shape[1]:], skip_special_tokens=True)


def build(base_dir: str, device: str = 'cpu', gpu_experts: int = 800, dtype: torch.dtype | None = None, verbose: bool = True) -> Stand:
    """Поднять мозг из несжатых весов: общая часть — на устройство, эксперты — остаются на диске.

    gpu_experts — сколько сработавших экспертов держать на устройстве (один ≈ 6 МБ в bf16, 12 МБ в float32).
    """
    from accelerate import init_empty_weights
    from accelerate.utils import set_module_tensor_to_device
    from safetensors import safe_open
    from transformers import AutoConfig, AutoTokenizer
    from transformers.models.qwen3_5_moe.modeling_qwen3_5_moe import Qwen3_5MoeForCausalLM

    base = Path(base_dir)
    dtype = dtype or (torch.bfloat16 if device != 'cpu' else torch.float32)
    config = AutoConfig.from_pretrained(base).text_config
    index = json.loads((base / 'model.safetensors.index.json').read_text(encoding='utf-8'))['weight_map']
    with init_empty_weights():
        model = Qwen3_5MoeForCausalLM(config)
    cache = ExpertCache(gpu_experts)
    blocks = tier(model, cache, lambda i: (DiskTensor(base / index[f'{LANG}layers.{i}.mlp.experts.gate_up_proj'], f'{LANG}layers.{i}.mlp.experts.gate_up_proj'),
                                           DiskTensor(base / index[f'{LANG}layers.{i}.mlp.experts.down_proj'], f'{LANG}layers.{i}.mlp.experts.down_proj')), dtype)
    by_file = collections.defaultdict(list)
    for name, file in index.items():
        if name == 'lm_head.weight' or name.startswith(LANG) and '.mlp.experts.' not in name:
            by_file[file].append(name)
    loaded = 0
    for file, names in sorted(by_file.items()):
        with safe_open(base / file, framework='pt') as f:
            for name in names:
                target = 'model.' + name[len(LANG):] if name.startswith(LANG) else name
                tensor = f.get_tensor(name)
                set_module_tensor_to_device(model, target, device, value=tensor, dtype=dtype)
                loaded += tensor.numel()
        if verbose:
            print(f'  {file}: общая часть {loaded / 1e9:.2f} млрд параметров', flush=True)
    left = [n for n, p in model.named_parameters() if p.device.type == 'meta']
    if left:
        raise RuntimeError(f'остались незагруженные веса: {left[:5]} (всего {len(left)})')
    model.eval()
    return Stand(model, AutoTokenizer.from_pretrained(base), blocks, cache, device)


# ---------------------------------------------------------------- проверка на крошечной копии архитектуры

def selfcheck() -> int:
    """Та же архитектура в миниатюре (случайные веса, процессор): ярусные эксперты должны считать ровно то же,
    что библиотечные; обучаемый эксперт — получать градиент, замороженные — нет; счётчик — сходиться с маршрутизатором."""
    from transformers.models.qwen3_5_moe.configuration_qwen3_5_moe import Qwen3_5MoeTextConfig
    from transformers.models.qwen3_5_moe.modeling_qwen3_5_moe import Qwen3_5MoeForCausalLM

    torch.manual_seed(7)
    config = Qwen3_5MoeTextConfig(vocab_size=500, hidden_size=64, num_hidden_layers=4, num_attention_heads=4, num_key_value_heads=2, head_dim=16,
                                  num_experts=16, num_experts_per_tok=4, moe_intermediate_size=32, shared_expert_intermediate_size=32,
                                  linear_num_key_heads=4, linear_num_value_heads=8, linear_key_head_dim=8, linear_value_head_dim=8,
                                  full_attention_interval=4, max_position_embeddings=512, mtp_num_hidden_layers=0)
    ok = True

    def check(name: str, good: bool, note: str = ''):
        nonlocal ok
        ok &= bool(good)
        print(f"  [{'ок' if good else 'ПЛОХО'}] {name}{' — ' + note if note else ''}")

    ref = Qwen3_5MoeForCausalLM(config).eval()
    for p in ref.parameters():   # у библиотеки часть весов создаётся пустой (torch.empty) — задаём сами
        if not torch.isfinite(p).all() or p.abs().max() > 1e3:
            nn.init.normal_(p, std=0.05)
    for layer in ref.model.layers:
        nn.init.normal_(layer.mlp.experts.gate_up_proj, std=0.05)
        nn.init.normal_(layer.mlp.experts.down_proj, std=0.05)
        nn.init.normal_(layer.mlp.gate.weight, std=0.5)
    ids = torch.randint(0, 500, (2, 24))
    with torch.no_grad():
        want = ref(ids).logits
    weights = [(l.mlp.experts.gate_up_proj.detach().clone(), l.mlp.experts.down_proj.detach().clone()) for l in ref.model.layers]
    cache = ExpertCache(budget=20)
    blocks = tier(ref, cache, lambda i: (RamTensor(weights[i][0]), RamTensor(weights[i][1])), torch.float32)
    stand = Stand(ref, None, blocks, cache, 'cpu')
    stand.record(True, reset=True)
    with torch.no_grad():
        got = ref(ids).logits
    check('ярусные эксперты считают то же, что библиотечные', torch.allclose(got, want, atol=1e-5), f'расхождение {float((got - want).abs().max()):.2e}')
    used = stand.usage()
    check('счётчик срабатываний: на каждое слово в каждом слое — ровно 4 эксперта', bool((used.sum(dim=1) == ids.numel() * 4).all()), f'по слоям {used.sum(dim=1).tolist()}')
    check('кэш держит не больше своего запаса и подкачивает недостающее', len(cache.items) <= 20 and cache.loads > 20, f'в кэше {len(cache.items)}, подкачек {cache.loads}')
    mask = torch.zeros_like(ids, dtype=torch.bool)
    mask[:, 16:] = True
    stand.record(True, reset=True, mask=mask)
    with torch.no_grad():
        ref(ids)
    check('счёт только по отмеченным словам (ответ без вопроса)', bool((stand.usage().sum(dim=1) == int(mask.sum()) * 4).all()), f'отмечено {int(mask.sum())} слов из {ids.numel()}')

    stand.record(False)
    layer, e = 2, int(used[2].argmax())
    for p in ref.parameters():
        p.requires_grad_(False)
    blocks[layer].unfreeze(e, 'cpu')
    loss = F.cross_entropy(ref(ids).logits[:, :-1].reshape(-1, 500), ids[:, 1:].reshape(-1))
    loss.backward()
    grads = [p.grad for p in blocks[layer].trained[str(e)]]
    check('обучаемый эксперт получает градиент, остальная модель заморожена',
          all(g is not None and float(g.abs().sum()) > 0 for g in grads) and sum(p.requires_grad for p in ref.parameters()) == 2)
    before = float(loss.detach())
    opt = torch.optim.SGD(blocks[layer].trained[str(e)], lr=0.5)
    for _ in range(8):
        opt.zero_grad()
        step = F.cross_entropy(ref(ids).logits[:, :-1].reshape(-1, 500), ids[:, 1:].reshape(-1))
        step.backward()
        opt.step()
    check('восемь шагов обучения одного эксперта снижают ошибку', float(step.detach()) < before, f'{before:.4f} → {float(step.detach()):.4f}')
    with torch.no_grad():
        other = blocks[0].frozen(0, 'cpu')[0]
    check('банк не тронут обучением', torch.equal(other, weights[0][0][0]) and torch.equal(weights[layer][0][e], blocks[layer].gate_up[e]))
    print('всё зелёное' if ok else 'ЕСТЬ ПРОВАЛЫ')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    if '--selfcheck' in sys.argv:
        sys.exit(selfcheck())
    print(__doc__)
