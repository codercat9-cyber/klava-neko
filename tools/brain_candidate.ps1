# Новый мозг-кандидат от закачки до отчёта, без присмотра: ждёт конца закачки (если идёт), перегоняет полные веса в GGUF
# (tools\brain_gguf.ps1, низкий приоритет), сдаёт экзамен (tools\brain_exam.ps1 — только когда Клава закрыта: на живом
# голосе видеокарту не делим, ждём) и сравнивает с прежним мозгом (data\mind\exam\<Baseline>.json). Живую Клаву не
# переключает — решение за другом, по отчёту.
#
#   powershell -ExecutionPolicy Bypass -File tools\brain_candidate.ps1 -Src models\brain\qwen36-huihui -Name qwen36-huihui
#       [-WaitPid 1234] [-Baseline hauhau] [-Quant Q6_K]
param(
  [Parameter(Mandatory)] [string]$Src,
  [Parameter(Mandatory)] [string]$Name,
  [int]$WaitPid = 0,
  [string]$Baseline = 'hauhau',
  [string]$Quant = 'Q6_K'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Say([string]$s) { "{0:HH:mm:ss} {1}" -f (Get-Date), $s }
try { (Get-Process -Id $PID).PriorityClass = 'BelowNormal' } catch { }

if ($WaitPid -gt 0 -and (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue)) {
  Say "жду конца закачки (PID $WaitPid)…"
  while (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 30 }
  Say 'закачка закончилась'
}

$gguf = Join-Path $root "models\brain\$Name\$Name-$Quant.gguf"
if (Test-Path $gguf) { Say "GGUF уже есть: $gguf" }
else {
  Say 'перегоняю в GGUF…'
  & (Join-Path $PSScriptRoot 'brain_gguf.ps1') -Src $Src -Name $Name -Quant $Quant
  if (-not (Test-Path $gguf)) { throw "GGUF не получился: $gguf" }
}

# экзамен — только без живой Клавы (голос и слух на той же видеокарте)
$said = $false
while (Get-Process KlavaDesk -ErrorAction SilentlyContinue) {
  if (-not $said) { Say 'Клава запущена — экзамен подождёт, пока её закроют'; $said = $true }
  Start-Sleep -Seconds 60
}
Say 'экзамен…'
& (Join-Path $PSScriptRoot 'brain_exam.ps1') -Model $gguf -Tag $Name
$base = Join-Path $root "data\mind\exam\$Baseline.json"
$cand = Join-Path $root "data\mind\exam\$Name.json"
if ((Test-Path $base) -and (Test-Path $cand)) { & (Join-Path $PSScriptRoot 'brain_exam.ps1') -Compare $base -With $cand }
Say 'готово — отчёт выше, живая Клава не переключена'
