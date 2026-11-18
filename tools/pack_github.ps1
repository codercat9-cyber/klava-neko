# Собрать чистую копию проекта Клавы для GitHub и друзей: без моделей, сборок, Python-окружений и личного (data,
# klava.json, свой облик web\config.json — вместо него web\config.default.json).
# Копия лежит в папке на рабочем столе и сама является git-репозиторием (.git в ней не трогается).
# В копию кладутся:
#   tools\manifest.txt — список файлов раздачи: по нему ОБНОВИТЬ.bat у друга убирает файлы, которых больше нет;
#   seed\tts\emo_lora — эмоции большого голоса (адаптер 77 МБ, без него голос просто без эмоций).
# -Zip — ещё и архив рядом с папкой (Klava-update-ДАТА.zip): его и скидывать друзьям — распаковал, запустил
#   ОБНОВИТЬ.bat (или УСТАНОВИТЬ.bat, если Клавы ещё нет).
# -Publish — сразу отправить на GitHub (tools\publish_github.ps1: приватный репозиторий, при первом запуске создаёт).
# Пуш по токену без GitHub CLI — tools\push_github.ps1 (кнопка «Запушить по токену» в пульте, или скрипт
# ОТПРАВИТЬ_НА_GITHUB.ps1, который кладётся в копию).
#
#   powershell -ExecutionPolicy Bypass -File tools\pack_github.ps1 [-Dest папка] [-Zip] [-Publish] [-Repo klava-neko]
param(
  [string]$Source = (Split-Path $PSScriptRoot -Parent),
  [string]$Dest = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Klava-GitHub'),
  [switch]$Zip,
  [switch]$Publish,
  [string]$Repo = 'klava-neko'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$Dest = (Resolve-Path $Dest).Path.TrimEnd('\')
$Source = (Resolve-Path $Source).Path.TrimEnd('\')
# скрипты автора — PowerShell с BOM (в .bat кириллица ломается); в git и в архив не идут
$bat = 'ОТПРАВИТЬ_НА_GITHUB.ps1'
$zipBat = 'СОБРАТЬ_АРХИВ_ДРУЗЬЯМ.ps1'
$oldBats = 'ОТПРАВИТЬ_НА_GITHUB.bat', 'СОБРАТЬ_АРХИВ_ДРУЗЬЯМ.bat'

# каталоги, которые не копируются (и не удаляются в копии — там живёт .git)
$xd = @(
  "$Source\models", "$Source\data", "$Source\bin", "$Source\editor_bin", "$Source\pult_bin", "$Source\runtime", "$Source\workspace",
  "$Source\app\KlavaPult\bin",
  "$Source\tts\.venv", "$Source\mineflayer_bridge\node_modules", "$Source\web\config.backups",
  "$Source\tts\voices\rejected", "$Source\tts\voices\old_mia",
  "$Source\app\KlavaDesk\bin", "$Source\app\KlavaEditor\bin",
  # её собственные вещи из мастерской (импортированные .glb) — личное
  "$Source\web\models",
  # игры: окружение Python (5 ГБ), веса нейронок, записи игры друга (личное), чужие NitroGen и драйвер геймпада
  "$Source\gamer\.venv", "$Source\gamer\models", "$Source\gamer\data", "$Source\gamer\logs",
  "$Source\gamer\nitrogen", "$Source\gamer\drivers",
  # Seed-VC (чужой репозиторий с весами, tts\nonverbal\convert.py) и страницы прослушки эмоций (70 МБ)
  "$Source\tools\seed-vc", "$Source\web\voices\emotions",
  # ACE-Step 1.5 (чужой репозиторий, своё окружение и ~9,4 ГБ весов — ставит setup_full.ps1)
  "$Source\tools\ace-step",
  # пакеты музыкального слуха (ставит tools\setup_full.ps1)
  "$Source\music\_pkgs",
  # 3D-редактор аватара копируется ниже своим robocopy — главный /MIR его в копии не трогает
  "$Dest\3D редактор аватара",
  # датасет голоса — тоже своим robocopy ниже (из data\emotion, который в главную копию не идёт)
  "$Dest\dataset",
  'obj', '__pycache__', '.git', '.claude', 'bin_*',
  # кэш и личные настройки Visual Studio / Rider (Klava.sln)
  '.vs', '.idea'
)
$xf = @('*.bak', '*.bak_*', '*.bak_before_*', '*.log', '*.pid', '*.part', '*.blend1', '*.user', '*.suo', 'klava.json', 'klava.json.*', 'remove_old_models.ps1',
        'klava_01.wav', 'klava_03.wav', 'klava_04.wav', 'klava_05.wav', 'klava_06.wav', $bat, $zipBat, $oldBats[0], $oldBats[1],
        "$Source\web\config.json", "$Source\web\config.default.last.json", "$Source\README_UPDATE_CLAUDE.md")
& robocopy $Source $Dest /MIR /XD @xd /XF @xf /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Host
if ($LASTEXITCODE -ge 8) { throw "robocopy не справился (код $LASTEXITCODE)" }

# исключённое robocopy в копии не стирает — остатки прошлых раздач (её вещи, настройки Claude Code, кэш Python)
# убираем сами (внутрь .git не заходим)
$stale = @(Get-ChildItem $Dest -Recurse -Directory -Force | Where-Object { $_.Name -in '.claude', '__pycache__', 'obj' } |
  Where-Object { $_.FullName.Substring($Dest.Length + 1) -notmatch '^\.git(\\|$)' } | Sort-Object { $_.FullName.Length } -Descending)
if (Test-Path "$Dest\web\models") { $stale += Get-Item "$Dest\web\models" }
foreach ($d in $stale) { if (Test-Path $d.FullName) { Remove-Item $d.FullName -Recurse -Force } }

# облик: свой config.json у каждого (там её вещи и наряды), в раздаче — дефолт «как у автора»
Copy-Item "$Source\web\config.json" "$Dest\web\config.default.json" -Force
foreach ($f in "$Dest\web\config.json", "$Dest\README_UPDATE_CLAUDE.md") { if (Test-Path $f) { Remove-Item $f -Force } }

# эмоции большого голоса — адаптер к Qwen3-TTS 1.7B с ударениями (ставит setup.ps1, если большой голос стоит)
$emo = "$Source\models\emo_lora"
if (Test-Path "$emo\adapter.safetensors") {
  New-Item -ItemType Directory -Force -Path "$Dest\seed\tts\emo_lora" | Out-Null
  Copy-Item "$emo\adapter.json", "$emo\adapter.safetensors" "$Dest\seed\tts\emo_lora" -Force
}

# 3D-редактор аватара с рабочего стола друга (Blender/VRoid-файлы, модель, текстуры, батники примерки) — отдельной
# папкой в репозитории; ярлыки .lnk указывают в его машину и не нужны
# (на рабочем столе или рядом с папкой копии — «ИИ кошкодевочка\3D редактор аватара»); не нашёлся — в копии остаётся прежний
$kit = @((Join-Path ([Environment]::GetFolderPath('Desktop')) '3D редактор аватара'), (Join-Path (Split-Path $Dest -Parent) '3D редактор аватара')) |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if ($kit) {
  & robocopy $kit (Join-Path $Dest '3D редактор аватара') /MIR /XF '*.lnk' '*.blend1' '*.bak' /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Host
  if ($LASTEXITCODE -ge 8) { throw "robocopy (3D-редактор) не справился (код $LASTEXITCODE)" }
}

# датасет голоса: готовые обучающие наборы эмоциональной LoRA (data\emotion\*.jsonl, ~27 МБ) и описание. Записи
# голосов друзей (data\speakers) сюда НЕ идут — чужие голоса остаются только на машине автора.
$emoData = Join-Path $Source 'data\emotion'
if (Test-Path $emoData) {
  $dsDest = Join-Path $Dest 'dataset\voice'
  & robocopy $emoData (Join-Path $dsDest 'emotion') '*.jsonl' /MIR /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Host
  if ($LASTEXITCODE -ge 8) { throw "robocopy (датасет голоса) не справился (код $LASTEXITCODE)" }
  $dsDoc = Join-Path $Source 'tts\emo_train\DATASET.md'
  if (Test-Path $dsDoc) { Copy-Item $dsDoc (Join-Path $dsDest 'README.md') -Force }
}

# скрипты автора — PowerShell в UTF-8 с BOM (Windows PowerShell 5.1 без BOM читает кириллицу как ANSI);
# запуск: правой кнопкой → «Выполнить с помощью PowerShell» или кнопкой в пульте. Старые .bat убираем.
foreach ($old in $oldBats) { $p = Join-Path $Dest $old; if (Test-Path $p) { Remove-Item $p -Force } }
$bom = New-Object Text.UTF8Encoding $true
[IO.File]::WriteAllLines((Join-Path $Dest $bat), @(
  '# Синхронизировать проект Клавы в эту папку и залить на GitHub по токену (спросит токен и имя/ссылку репозитория).',
  "& '$Source\tools\pack_github.ps1' -Dest `$PSScriptRoot",
  "if (`$?) { & (Join-Path `$PSScriptRoot 'tools\push_github.ps1') -Dir `$PSScriptRoot }",
  "Read-Host 'Enter — закрыть'"), $bom)
[IO.File]::WriteAllLines((Join-Path $Dest $zipBat), @(
  '# Синхронизировать проект Клавы в эту папку и собрать рядом Klava-update-ДАТА.zip — его и скидывать друзьям.',
  "& '$Source\tools\pack_github.ps1' -Dest `$PSScriptRoot -Zip",
  "Read-Host 'Enter — закрыть'"), $bom)

# список файлов раздачи (пути от корня) — по нему обновление у друга убирает то, чего больше нет
$skip = @($bat, $zipBat, 'tools\manifest.txt')
$files = @(Get-ChildItem $Dest -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\\.git(\\|$)' } |
  Where-Object { $skip -notcontains $_.FullName.Substring($Dest.Length + 1) })
$rels = $files | ForEach-Object { $_.FullName.Substring($Dest.Length + 1) } | Sort-Object
[IO.File]::WriteAllLines((Join-Path $Dest 'tools\manifest.txt'), [string[]]$rels, (New-Object Text.UTF8Encoding $false))

"копия: $Dest — {0} файлов, {1:N1} МБ" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)
$big = $files | Where-Object Length -gt 95MB
if ($big) { throw "файлы больше 95 МБ (GitHub не примет): $($big.FullName -join ', ')" }

if ($Zip) {
  # архив для друзей: папка Klava-update-ДАТА внутри (распаковка не смешается со старой Клавой)
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $top = 'Klava-update-' + (Get-Date -Format 'yyyy-MM-dd')
  $zipPath = Join-Path (Split-Path $Dest -Parent) "$top.zip"
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
  $all = @($files) + @(Get-Item (Join-Path $Dest 'tools\manifest.txt'))
  $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($f in $all) {
      $name = "$top/" + $f.FullName.Substring($Dest.Length + 1).Replace('\', '/')
      [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
  } finally { $archive.Dispose() }
  "архив для друзей: $zipPath ({0:N1} МБ) — распаковать куда угодно и запустить ОБНОВИТЬ.bat" -f ((Get-Item $zipPath).Length / 1MB)
}

if ($Publish) { & (Join-Path $Dest 'tools\publish_github.ps1') -Dir $Dest -Repo $Repo }
# код выхода — свой: иначе наружу утекает код robocopy (1 = «скопировал», это не ошибка)
exit 0
