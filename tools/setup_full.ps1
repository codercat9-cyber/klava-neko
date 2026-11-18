# Большой комплект слуха и голоса Клавы — для машины с видеокартой NVIDIA от 8 ГБ:
#   голос Qwen3-TTS (клон её эталона) + ударения, слух whisper large-v3-turbo на видеокарте, профили WavLM.
# Ставит: Python 3.12 (если нет), окружение tts\.venv с torch под CUDA, модели (fetch_models.ps1), whisper.cpp с CUDA,
# для каверов — Seed-VC и ACE-Step 1.5 (своё окружение tools\ace-step).
# Без видеокарты не нужен: лёгкий комплект (fetch_lite.ps1) слушает и говорит на процессоре.
#
#   powershell -ExecutionPolicy Bypass -File tools\setup_full.ps1
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$vram = 0
try { $vram = [int](& nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1) } catch { }
if ($vram -lt 7500) {
  "Видеокарты NVIDIA от 8 ГБ не видно (nvidia-smi: $vram МБ). Большой комплект тут не потянет — хватит лёгкого (он уже стоит)."
  exit 1
}
"NVIDIA: $vram МБ видеопамяти"

# Python 3.12 — через py-лаунчер; нет — winget или установщик с python.org (tools\deps.ps1)
. (Join-Path $PSScriptRoot 'deps.ps1')
Ensure-Python312
$py = 'py'
Ensure-Git $Root   # Seed-VC и ACE-Step — свои репозитории на GitHub

$venv = Join-Path $Root 'tts\.venv'
if (-not (Test-Path "$venv\Scripts\python.exe")) {
  "создаю окружение $venv…"
  & $py -3.12 -m venv $venv
  if ($LASTEXITCODE) { throw 'venv не создался' }
}
$vpy = "$venv\Scripts\python.exe"
& $vpy -m pip install --upgrade pip
# torch под CUDA 12.8 — работает с драйверами NVIDIA от 570; голосу нужна видеокарта
& $vpy -m pip install torch==2.10.0 torchaudio==2.10.0 --index-url https://download.pytorch.org/whl/cu128
if ($LASTEXITCODE) { throw 'torch не поставился' }
& $vpy -m pip install -r (Join-Path $Root 'tts\requirements-full.txt')
if ($LASTEXITCODE) { throw 'пакеты голоса не поставились' }
# музыкальный слух и пение: свои пакеты — отдельной папкой music\_pkgs (numpy/torch голоса не трогаются: --no-deps)
& $vpy -m pip install --target (Join-Path $Root 'music\_pkgs') --no-deps --upgrade demucs==4.0.1 dora-search julius openunmix lameenc `
  treetable retrying submitit cloudpickle pyworld praat-parselmouth pretty_midi mido mir_eval resampy rotary-embedding-torch basic-pitch==0.4.0 `
  importlib_resources zipp https://github.com/CPJKU/beat_this/archive/main.zip
if ($LASTEXITCODE) { throw 'пакеты музыки не поставились' }
# пение её тембром: Seed-VC (GPL-3.0, свой репозиторий в tools\seed-vc; веса он сам берёт с Hugging Face при первом кавере)
$seed = Join-Path $Root 'tools\seed-vc'
if (-not (Test-Path (Join-Path $seed 'inference.py'))) {
  & git clone https://github.com/Plachtaa/seed-vc $seed
  if ($LASTEXITCODE) { throw 'Seed-VC не склонировался (нужен git)' }
  & git -C $seed checkout 51383efd921027683c89e5348211d93ff12ac2a8
}
& $vpy -m pip install --target (Join-Path $seed '_pkgs') --no-deps --upgrade munch matplotlib contourpy cycler fonttools kiwisolver `
  pyparsing python-dateutil six descript-audio-codec==1.0.0
if ($LASTEXITCODE) { throw 'пакеты Seed-VC не поставились' }
# русские каверы живым вокалом: ACE-Step 1.5 (MIT) — свой репозиторий и своё окружение в tools\ace-step (у него свои
# torch и transformers, окружение голоса не трогается); веса (~9,4 ГБ) он качает сам при первом кавере. Не встал —
# каверы поются прежним путём (её речь по нотам), остальное работает.
$ace = Join-Path $Root 'tools\ace-step'
try {
  if (-not (Test-Path (Join-Path $ace 'pyproject.toml'))) {
    & git clone https://github.com/ACE-Step/ACE-Step-1.5 $ace
    if ($LASTEXITCODE) { throw 'ACE-Step не склонировался (нужен git)' }
    & git -C $ace checkout ca1e85fe9430179831e6bc6be790c332190a3866
  }
  & $py -3.12 -m pip install --user --upgrade uv
  Push-Location $ace
  try { & $py -3.12 -m uv sync } finally { Pop-Location }
  if ($LASTEXITCODE) { throw 'окружение ACE-Step не собралось' }
  'ACE-Step готов: русские каверы — живым вокалом'
} catch {
  "ACE-Step не встал ($_) — русские каверы будут прежним путём"
}

# голос 1.7B с адаптером ударений просит ~4,5 ГБ видеопамяти вместе со слухом; меньше 7 ГБ — лёгкая 0.6B
$vram = 0
try { $vram = [int]((& nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits | Select-Object -First 1).Trim()) } catch { }
if ($vram -gt 0 -and $vram -lt 7000) {
  "видеопамяти $vram МБ — ставлю голос поменьше (0.6B, ударения хуже)"
  & (Join-Path $PSScriptRoot 'fetch_models.ps1') -Root $Root -SmallVoice
} else {
  & (Join-Path $PSScriptRoot 'fetch_models.ps1') -Root $Root
}

# whisper.cpp с CUDA: самый свежий релиз, где есть сборка cublas для Windows x64 (уже стоит — при обновлении не перекачиваю)
if (Test-Path (Join-Path $Root 'runtime\whisper\whisper-server.exe')) { 'whisper.cpp уже стоит' } else {
$headers = @{ 'User-Agent' = 'klava-setup' }
$rel = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=10' -Headers $headers |
  Where-Object { $_.assets | Where-Object name -like 'whisper-cublas-12*-bin-x64.zip' } | Select-Object -First 1
if (-not $rel) { throw 'в релизах whisper.cpp нет сборки cublas для Windows' }
$asset = $rel.assets | Where-Object name -like 'whisper-cublas-12*-bin-x64.zip' | Select-Object -First 1
$dest = Join-Path $Root 'runtime\whisper'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$zip = Join-Path $env:TEMP $asset.name
"качаю $($asset.name) ({0:N0} МБ)…" -f ($asset.size / 1MB)
& curl.exe -L --fail --silent --show-error --retry 5 -o $zip $asset.browser_download_url
if ($LASTEXITCODE -ne 0) { throw "не скачалось: $($asset.name)" }
Expand-Archive -Path $zip -DestinationPath $dest -Force
Remove-Item $zip
$srv = Get-ChildItem $dest -Recurse -Filter whisper-server.exe | Select-Object -First 1
if (-not $srv) { throw 'в архиве whisper.cpp нет whisper-server.exe' }
if ($srv.DirectoryName -ne $dest) { Get-ChildItem $srv.DirectoryName | Move-Item -Destination $dest -Force }
"whisper.cpp $($rel.tag_name) → $dest"
}

$desk = Join-Path $Root 'bin\KlavaDesk.exe'
if (Test-Path $desk) { $null = Invoke-Klava $desk @('--set', 'speechBackend=auto', 'sttExe=runtime\whisper\whisper-server.exe', 'ttsPython=tts\.venv\Scripts\python.exe') }
'Большой комплект готов: при следующем запуске Клава сама возьмёт его (speechBackend = auto).'
