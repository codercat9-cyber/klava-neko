# Установка и обновление Клавы (Windows 10 версии 2004+ / Windows 11, x64). Запускается из УСТАНОВИТЬ.bat и ОБНОВИТЬ.bat.
#   1) зависимости — .NET 9 SDK, WebView2, Visual C++ рантайм, русское распознавание текста (winget, а если его нет —
#      официальные установщики напрямую);
#   2) сборка KlavaDesk (bin) и редактора аватара (editor_bin);
#   3) облик (web\config.json): свежая установка — из config.default.json; обновление — свои вещи и наряды остаются,
#      новое доезжает;
#   4) лёгкий комплект слуха и голоса (~350 МБ, процессор) — работает на любом ноутбуке;
#   5) память: векторы смысла bge-m3 (~0,6 ГБ, процессор) и llama.cpp для них;
#   6) данные из seed: блокноты игр, карточки треков, песни для танцев (качаются из открытых источников);
#   7) по желанию: свой локальный мозг (Qwen3.6, 11–41 ГБ), большой голос (NVIDIA от 8 ГБ) с эмоциями, Blender;
#   8) ключи облачных мозгов — в переменные окружения пользователя, в файлы не пишутся;
#   9) ярлык «Клава» на рабочем столе.
# -Update — режим обновления: ничего не спрашивает, обновляет только то, что уже стоит (свой мозг, большой голос),
#   личное (data, models, klava.json, свой облик web\config.json) не трогает.
# -NoShortcut — не трогать ярлык «Клава» на рабочем столе (проверка установки в песочнице)
param([switch]$Yes, [switch]$Update, [switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'deps.ps1')
function Ask([string]$q) { if ($Yes) { return $true }; if ($Update) { return $false }; $a = Read-Host "$q (да/нет)"; return $a -match '^(да|д|y|yes)$' }
function Step([string]$s) { ''; "=== $s ===" }
# скачанные из интернета скрипты Windows помечает «из интернета» — снимаем, чтобы запускались без вопросов
Get-ChildItem (Join-Path $root 'tools') -Filter *.ps1 | Unblock-File -ErrorAction SilentlyContinue
Get-ChildItem $root -Filter *.bat | Unblock-File -ErrorAction SilentlyContinue

Step 'Проверка системы'
Assert-Windows
(Get-CimInstance Win32_VideoController | ForEach-Object { "видеокарта: $($_.Name)" })

Step 'Зависимости'
Ensure-DotNet9
Ensure-WebView2
Ensure-VCRedist
Ensure-OcrRu

Step 'Сборка Клавы'
Stop-Klava $root
$desk = Join-Path $root 'bin\KlavaDesk.exe'
& dotnet build (Join-Path $root 'app\KlavaDesk\KlavaDesk.csproj') -c Release -o (Join-Path $root 'bin') --nologo -v q
if ($LASTEXITCODE) { throw 'KlavaDesk не собрался — пришли этот текст автору' }
& dotnet build (Join-Path $root 'app\KlavaEditor\KlavaEditor.csproj') -c Release -o (Join-Path $root 'editor_bin') --nologo -v q
if ($LASTEXITCODE) { 'редактор аватара не собрался — Клаве он не нужен, идём дальше' }
$pult = Join-Path $root 'pult_bin\KlavaPult.exe'
if (Test-Path (Join-Path $root 'app\KlavaPult\KlavaPult.csproj')) {
  & dotnet build (Join-Path $root 'app\KlavaPult\KlavaPult.csproj') -c Release -o (Join-Path $root 'pult_bin') --nologo -v q
  if ($LASTEXITCODE) { 'пульт не собрался — Клава работает и без него, идём дальше' }
}

Step 'Облик (наряды, вещи, настройки аватара)'
$web = Join-Path $root 'web'
$look = Join-Path $web 'config.json'
$lookDefault = Join-Path $web 'config.default.json'
# дефолт, с которым свой облик слит в последний раз: по нему видно, что друг (или она сама) поменял, а что нет.
# Пишется только после удачного слияния — сорвалось обновление посередине, следующее сольёт от той же точки
$lookLast = Join-Path $web 'config.default.last.json'
if (-not (Test-Path $lookDefault)) { 'нет web\config.default.json — облик не трогаю' }
elseif (-not (Test-Path $look)) { Copy-Item $lookDefault $look; Copy-Item $lookDefault $lookLast -Force; 'облик: новый, как у автора' }
else {
  $base = if (Test-Path $lookLast) { $lookLast } else { '-' }
  if ((Invoke-Klava $desk @('--web-config-merge', $base, $look, $lookDefault, $look)) -eq 0) { Copy-Item $lookDefault $lookLast -Force }
  else { 'облик не слился — остался прежний (прошлый config.json — в web\config.backups)' }
}

Step 'Лёгкий слух и голос (процессор)'
& (Join-Path $PSScriptRoot 'fetch_lite.ps1') -Root $root

Step 'Память: векторы смысла (bge-m3, ~0,6 ГБ, процессор)'
'Граф памяти ищет по смыслу, а не только по словам («что я говорил про работу» найдёт и «смену», и «начальника»).'
$embed = Join-Path $root 'models\embed\bge-m3-q8_0.gguf'
try {
  if (Test-Path $embed) { '  bge-m3: есть' }
  else { '  качаю bge-m3…'; Get-File 'https://huggingface.co/ggml-org/bge-m3-Q8_0-GGUF/resolve/main/bge-m3-q8_0.gguf' $embed; '  bge-m3: готово' }
  Ensure-Llama -Root $root
} catch { "  не вышло ($_) — память будет искать только по словам, остальное работает" }

Step 'Данные: блокноты игр, песни для танцев'
$data = Join-Path $root 'data'
# блокноты игр (коды шкафчиков DOOM 3, правила Бакшота) — справочные, обновляются всегда
$guide = Join-Path $data 'games\guide'
New-Item -ItemType Directory -Force -Path $guide | Out-Null
Copy-Item (Join-Path $root 'seed\games\guide\*.md') $guide -Force
"  блокноты игр: $((Get-ChildItem $guide -Filter *.md).Count)"
# фонотека: карточки треков (доли и части песни для танца) и сами песни — из открытых источников, если ещё нет
$lib = Join-Path $data 'music\library'
New-Item -ItemType Directory -Force -Path $lib | Out-Null
Copy-Item (Join-Path $root 'seed\music\*.track.json') $lib -Force
$songs = Get-Content (Join-Path $root 'seed\music\library.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($s in @($songs)) {
  $dst = Join-Path $lib $s.file
  if ((Test-Path $dst) -and (Get-Item $dst).Length -eq [long]$s.size) { "  есть: $($s.file)"; continue }
  "  качаю: $($s.file) ({0:N1} МБ) — $($s.why)" -f ($s.size / 1MB)
  try { Get-File $s.url $dst; "  готово" } catch { "  не скачалось ($_) — танец будет под свой счёт, без песни" }
}

Step 'Свой локальный мозг (по желанию)'
$brainDir = Join-Path $root 'models\brain'
$hasBrain = (Test-Path $brainDir) -and (Get-ChildItem $brainDir -Recurse -Filter *.gguf -ErrorAction SilentlyContinue)
if ($Update) {
  if ($hasBrain) {
    'свой мозг стоит — докачиваю модель, если не докачана (llama.cpp уже есть — не трогаю)'
    & (Join-Path $PSScriptRoot 'fetch_brain.ps1') -Root $root -SkipRuntime:(Test-Path (Join-Path $root 'runtime\llama\llama-server.exe'))
  }
  else { 'не стоит — пропускаю (поставить: УСТАНОВИТЬ.bat)' }
} else {
  'Облачные мозги (DeepSeek, Gemini, Antigravity) быстрые, но нужен интернет и ключ. Свой — Qwen3.6-35B-A3B uncensored:'
  'без интернета и цензуры, но качать 11–41 ГБ (по объёму ОЗУ) и отвечает медленнее облака.'
  if (Ask 'Поставить свой локальный мозг?') { & (Join-Path $PSScriptRoot 'fetch_brain.ps1') -Root $root }
}

Step 'Большой голос (видеокарта NVIDIA от 8 ГБ, по желанию)'
$vram = 0
try { $vram = [int](& nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1) } catch { }
$hasFull = Test-Path (Join-Path $root 'tts\.venv\Scripts\python.exe')
if ($vram -ge 7500) {
  if ($Update) {
    if ($hasFull) { 'большой голос стоит — докачиваю новое, если появилось'; & (Join-Path $PSScriptRoot 'setup_full.ps1') -Root $root }
    else { 'не стоит — пропускаю (поставить: УСТАНОВИТЬ.bat)' }
  } else {
    'Голос — клон её эталона (Qwen3-TTS 1.7B с адаптером ударений), слух — whisper на видеокарте. Качать ~7 ГБ + Python-окружение ~6 ГБ.'
    if ($hasFull -or (Ask 'Поставить большой голос?')) { & (Join-Path $PSScriptRoot 'setup_full.ps1') -Root $root }
  }
  # эмоции голоса: адаптер (LoRA), выученный на актёрской русской речи, — к модели 1.7B с ударениями
  $emoSeed = Join-Path $root 'seed\tts\emo_lora'
  $emo = Join-Path $root 'models\emo_lora'
  $base = if (Test-Path (Join-Path $emoSeed 'adapter.json')) { (Get-Content (Join-Path $emoSeed 'adapter.json') -Raw -Encoding UTF8 | ConvertFrom-Json).base } else { '' }
  if (-not $base) { 'эмоции голоса: в этой раздаче их нет' }
  elseif (-not (Test-Path (Join-Path $root "models\$base\model.safetensors"))) { "эмоции голоса: нет модели $base — без них (голос поменьше)" }
  elseif ((Test-Path (Join-Path $emo 'adapter.safetensors')) -and (Get-FileHash (Join-Path $emo 'adapter.safetensors')).Hash -eq (Get-FileHash (Join-Path $emoSeed 'adapter.safetensors')).Hash) { 'эмоции голоса: есть' }
  else {
    if (Test-Path $emo) { $old = "$($emo)_old"; Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue; Move-Item $emo $old }
    New-Item -ItemType Directory -Force -Path $emo | Out-Null
    Copy-Item (Join-Path $emoSeed '*') $emo -Force
    'эмоции голоса: поставлены (злость, шёпот, страх, грусть, смех в речи)'
  }
} else { "видеокарты NVIDIA от 8 ГБ нет ($vram МБ) — остаётся лёгкий голос, он уже стоит" }

Step 'Blender для мастерской (по желанию)'
if (Find-Blender) { "есть: $(Find-Blender)" }
elseif (Ask 'Поставить Blender (~400 МБ)? С ним Клава сама собирает сложные 3D-вещи и одежду по своей фигуре') {
  if (-not (Winget-Install 'BlenderFoundation.Blender')) { 'winget нет — Blender можно поставить с blender.org, Клава найдёт его сама' }
} else { 'пропускаю — простые вещи и одежду Клава шьёт и без него' }

Step 'Minecraft (по желанию)'
if (Get-Command node -ErrorAction SilentlyContinue) {
  'Клава заходит в Minecraft отдельным игроком (mineflayer, Node.js) — ставлю его библиотеки (~20 МБ).'
  Push-Location (Join-Path $root 'mineflayer_bridge')
  & npm install --no-audit --no-fund --loglevel=error
  if ($LASTEXITCODE) { 'библиотеки Minecraft не поставились — остальное работает, позже: npm install в папке mineflayer_bridge' }
  Pop-Location
} else { 'Node.js нет — Minecraft пропускаю (поставишь с nodejs.org, потом npm install в папке mineflayer_bridge)' }

if (-not $Update) {
  Step 'Ключи облачных мозгов'
  'Ключ хранится в переменной окружения пользователя (в файлы Клавы и в git не попадает). Enter — пропустить.'
  foreach ($k in @(@{ Name = 'DEEPSEEK_API_KEY'; What = 'DeepSeek (platform.deepseek.com)' }, @{ Name = 'GEMINI_API_KEY'; What = 'Gemini (aistudio.google.com)' })) {
    if ([Environment]::GetEnvironmentVariable($k.Name, 'User')) { "$($k.Name): уже задан"; continue }
    if ($Yes) { continue }
    $v = Read-Host "Ключ $($k.What)"
    if ($v.Trim()) { [Environment]::SetEnvironmentVariable($k.Name, $v.Trim(), 'User'); "$($k.Name): сохранён" }
  }
}

if (-not $NoShortcut) {
  Step 'Ярлык на рабочем столе'
  $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Клава.lnk'
  $ws = New-Object -ComObject WScript.Shell
  $s = $ws.CreateShortcut($lnk)
  $s.TargetPath = $desk
  $s.WorkingDirectory = Split-Path $desk
  $s.IconLocation = "$desk,0"
  $s.Description = 'Клава — кошкодевочка на рабочем столе'
  $s.Save()
  "ярлык: $lnk"
  if (Test-Path $pult) {
    $plnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Пульт Клавы.lnk'
    $p = $ws.CreateShortcut($plnk)
    $p.TargetPath = $pult
    $p.WorkingDirectory = Split-Path $pult
    $p.IconLocation = "$pult,0"
    $p.Description = 'Пульт Клавы: запуск, мозги, команды, сборка, GitHub'
    $p.Save()
    "ярлык: $plnk"
  }
}

Step 'Проверка'
$null = Invoke-Klava $desk @('--lite-test') -Tail 3
''
if ($Update) { 'Обновлено! Запуск — ярлык «Клава».' }
else {
  'Готово! Запуск — ярлык «Клава» или START_KLAVA.bat. Говори с ней голосом, зажми правый Ctrl — рация, Ctrl+Alt+M — написать.'
  'Мозг, голос, микрофон — правый клик по Клаве.'
}
