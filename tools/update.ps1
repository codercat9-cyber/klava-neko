# Обновление Клавы у друга. Запускается из ОБНОВИТЬ.bat в папке НОВОЙ версии (распакованный архив или git clone).
#   1) находит установленную Клаву: эта же папка (git clone / распаковали поверх), запущенная Клава, ярлык «Клава»
#      на рабочем столе, соседние папки; не нашла — спросит путь (Enter — поставить начисто сюда);
#   2) закрывает её (память записывается);
#   3) git clone — git pull; иначе переносит новый код поверх старой Клавы. Личное не трогается: data (память, голоса
#      друзей, журналы), models, bin, runtime, klava.json, свой облик web\config.json, её вещи web\models, окружения
#      Python. Файлы прошлой версии, которых в новой нет, убираются (по списку tools\manifest.txt);
#   4) setup.ps1 -Update: зависимости (.NET 9, WebView2, VC++), сборка, новые модели и песни, облик (свои вещи и наряды
#      остаются, новое доезжает), свой мозг и большой голос — только если уже стоят;
#   5) запускает Клаву.
#
#   powershell -ExecutionPolicy Bypass -File tools\update.ps1 [-Target папка_старой_Клавы] [-NoStart] [-FilesOnly]
# -FilesOnly — только перенести файлы (без сборки, докачки и запуска): проверка обновления
param([string]$Target = '', [switch]$NoStart, [switch]$FilesOnly)
$ErrorActionPreference = 'Stop'
$pkg = (Resolve-Path (Split-Path $PSScriptRoot -Parent)).Path.TrimEnd('\')
. (Join-Path $PSScriptRoot 'deps.ps1')
function Step([string]$s) { ''; "=== $s ===" }

function Test-Install([string]$d) {
  if (-not $d -or -not (Test-Path (Join-Path $d 'app\KlavaDesk\KlavaDesk.csproj'))) { return $false }
  return (Test-Path (Join-Path $d 'bin\KlavaDesk.exe')) -or (Test-Path (Join-Path $d 'data')) -or (Test-Path (Join-Path $d 'klava.json'))
}
# корень Клавы по пути к bin\KlavaDesk.exe
function Root-Of([string]$exe) {
  if (-not $exe -or (Split-Path $exe -Leaf) -ne 'KlavaDesk.exe') { return $null }
  return Split-Path (Split-Path $exe -Parent) -Parent
}
function Find-Install {
  if (Test-Install $pkg) { return $pkg }
  foreach ($p in @(Get-Process KlavaDesk -ErrorAction SilentlyContinue)) {
    try { $d = Root-Of $p.Path; if (Test-Install $d) { return $d } } catch { }
  }
  $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Клава.lnk'
  if (Test-Path $lnk) {
    $d = Root-Of ((New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath)
    if (Test-Install $d) { return $d }
  }
  # архив распаковали рядом со старой Клавой
  foreach ($d in @(Get-ChildItem (Split-Path $pkg -Parent) -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)) {
    if ($d.FullName.TrimEnd('\') -ne $pkg -and (Test-Install $d.FullName)) { return $d.FullName }
  }
  return $null
}
function Read-Manifest([string]$root) {
  $f = Join-Path $root 'tools\manifest.txt'
  if (-not (Test-Path $f)) { return $null }
  $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach ($l in (Get-Content $f -Encoding UTF8)) { if ($l.Trim()) { [void]$set.Add($l.Trim()) } }
  return ,$set
}

Step 'Где стоит Клава'
if ($Target) {
  if (-not (Test-Install $Target)) { throw "в «$Target» нет установленной Клавы (нужны app\KlavaDesk и bin\KlavaDesk.exe или data)" }
} else { $Target = Find-Install }
if (-not $Target) {
  'Установленную Клаву не нашла (ни ярлыка «Клава», ни запущенной, ни папки рядом).'
  $a = (Read-Host 'Путь к папке старой Клавы (где УСТАНОВИТЬ.bat). Enter — поставить начисто в эту папку').Trim().Trim('"')
  if (-not $a) { & (Join-Path $PSScriptRoot 'setup.ps1'); return }
  if (-not (Test-Install $a)) { throw "в «$a» нет установленной Клавы" }
  $Target = $a
}
$Target = (Resolve-Path $Target).Path.TrimEnd('\')
$inPlace = $Target -eq $pkg
if ($inPlace) { "обновляю на месте: $Target" } else { "новая версия: $pkg`nстарая Клава: $Target (личное остаётся на месте)" }

Step 'Закрываю Клаву'
Stop-Klava $Target

$newList = Read-Manifest $pkg
$removed = 0

if ($inPlace -and (Test-Path (Join-Path $Target '.git'))) {
  Step 'git pull'
  Ensure-Git $Target
  # web\config.json раньше лежал в репозитории; теперь он свой у каждого. Прячем его на время pull, чтобы git
  # не стёр и не упёрся в «local changes would be overwritten»
  $look = Join-Path $Target 'web\config.json'
  $lookSave = Join-Path $env:TEMP "klava_config_$PID.json"
  if (Test-Path $look) {
    Copy-Item $look $lookSave -Force
    & git -C $Target ls-files --error-unmatch web/config.json 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { & git -C $Target checkout -- web/config.json }
  }
  & git -C $Target pull --ff-only --autostash
  $pullCode = $LASTEXITCODE
  if (Test-Path $lookSave) { Copy-Item $lookSave $look -Force; Remove-Item $lookSave }
  if ($pullCode) { throw 'git pull не прошёл — пришли этот текст автору (свои файлы не тронуты)' }
  $newList = Read-Manifest $Target
}
elseif (-not $inPlace) {
  Step 'Переношу новую версию'
  $oldList = Read-Manifest $Target
  # из новой папки не берётся ничего личного, даже если в ней уже запускали Клаву
  $xd = @('.git', '__pycache__', 'obj', 'bin_*') + @(
    'data', 'models', 'bin', 'editor_bin', 'runtime', 'workspace', 'tts\.venv', 'web\config.backups', 'web\models',
    'mineflayer_bridge\node_modules', 'music\_pkgs', 'tools\seed-vc', 'tools\ace-step',
    'gamer\.venv', 'gamer\models', 'gamer\data', 'gamer\logs', 'gamer\nitrogen', 'gamer\drivers',
    'app\KlavaDesk\bin', 'app\KlavaEditor\bin' | ForEach-Object { Join-Path $pkg $_ })
  $xf = @('klava.json', 'klava.json.*', '*.log', '*.pid', (Join-Path $pkg 'web\config.json'), (Join-Path $pkg 'web\config.default.last.json'),
          (Join-Path $pkg 'ОТПРАВИТЬ_НА_GITHUB.bat'))
  & robocopy $pkg $Target /E /XD @xd /XF @xf /NFL /NDL /NJH /NP /R:2 /W:2 | Out-Host
  if ($LASTEXITCODE -ge 8) { throw "robocopy не справился (код $LASTEXITCODE) — Клава закрыта, файлы заняты? Запусти ОБНОВИТЬ.bat ещё раз" }
  # файлы прошлой версии, которых в новой нет (переименованные и выкинутые), — только из списка прошлой раздачи
  if ($oldList -and $newList) {
    foreach ($rel in $oldList) {
      if ($newList.Contains($rel)) { continue }
      $f = Join-Path $Target $rel
      if (Test-Path -LiteralPath $f -PathType Leaf) { Remove-Item -LiteralPath $f -Force; $removed++ }
    }
  }
}

# старые .cs, которых в новой версии нет, ломают сборку (два одинаковых класса) — убираются всегда, даже без списка
if ($newList) {
  foreach ($proj in 'app\KlavaDesk', 'app\KlavaEditor') {
    $dir = Join-Path $Target $proj
    if (-not (Test-Path $dir)) { continue }
    foreach ($cs in @(Get-ChildItem $dir -Recurse -File -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
      $rel = $cs.FullName.Substring($Target.Length + 1)
      if (-not $newList.Contains($rel)) { Remove-Item -LiteralPath $cs.FullName -Force; $removed++; "  убран устаревший код: $rel" }
    }
  }
}
# батник публикации автора (ездил в старых раздачах) указывает в его машину — у друга он не нужен
$sendBat = Join-Path $Target 'ОТПРАВИТЬ_НА_GITHUB.bat'
if (Test-Path $sendBat) {
  $m = Select-String -Path $sendBat -Pattern '-File "([^"]+pack_github\.ps1)"' | Select-Object -First 1
  if ($m -and -not (Test-Path $m.Matches[0].Groups[1].Value)) { Remove-Item $sendBat -Force; $removed++ }
}
if ($removed) { "убрано устаревших файлов: $removed" }

if ($FilesOnly) { ''; 'файлы перенесены (-FilesOnly): сборку и докачку не запускаю'; return }
Step 'Обновление: зависимости, сборка, облик, модели'
& (Join-Path $Target 'tools\setup.ps1') -Update

if (-not $NoStart) {
  Step 'Запуск'
  $desk = Join-Path $Target 'bin\KlavaDesk.exe'
  Start-Process -FilePath $desk -WorkingDirectory (Split-Path $desk)
  'Клава запущена. Если что-то не так — пришли автору data\logs\klava.log.'
}
if (-not $inPlace) { ''; "Папку с новой версией ($pkg) можно удалить — всё уже в $Target." }
