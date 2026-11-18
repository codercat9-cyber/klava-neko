# Свой локальный мозг Клавы: Qwen3.6-35B-A3B uncensored (MoE: 35B знаний, 3B считается на токен) + llama.cpp.
# Квант — по объёму ОЗУ (эксперты живут в ОЗУ, общие слои — на видеокарте; сколько влезет, решает --fit),
# сборка llama.cpp — по видеокарте: NVIDIA → CUDA 12.4, AMD/Intel → Vulkan, нет видеокарты → CPU.
#
#   powershell -ExecutionPolicy Bypass -File tools\fetch_brain.ps1 [-Quant Q4_K_P] [-SkipRuntime]
param([string]$Quant = '', [switch]$SkipRuntime, [string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deps.ps1')
$repo = 'HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive'
$prefix = 'Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive'
$ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)

if (-not $Quant) {
  # файл модели + Windows, браузер и сама Клава должны влезть в ОЗУ
  $Quant = if ($ram -ge 56) { 'Q6_K_P' } elseif ($ram -ge 40) { 'Q4_K_P' } elseif ($ram -ge 28) { 'IQ3_M' } elseif ($ram -ge 20) { 'IQ2_M' } else { '' }
}
if (-not $Quant) {
  "ОЗУ $ram ГБ — этому мозгу мало (нужно от 20 ГБ). Оставайся на облачных мозгах: DeepSeek, Gemini, Antigravity."
  exit 1
}
"ОЗУ $ram ГБ → квант $Quant"

$dir = Join-Path $Root 'models\brain\qwen36'
$model = "$prefix-$Quant.gguf"
$mmproj = "mmproj-$prefix-f16.gguf"
& (Join-Path $PSScriptRoot 'hf_get.ps1') -Repo $repo -File $mmproj -OutDir $dir -Parts 4   # сбой — исключение, скрипт встанет
& (Join-Path $PSScriptRoot 'hf_get.ps1') -Repo $repo -File $model -OutDir $dir -Parts 12

$exe = 'runtime\llama\llama-server.exe'
if (-not $SkipRuntime) {
  Ensure-Llama -Root $Root -Force
} else {
  $exe = ''
}

$desk = Join-Path $Root 'bin\KlavaDesk.exe'
if (Test-Path $desk) {
  $set = @("brainModel=models\brain\qwen36\$model", "brainMmproj=models\brain\qwen36\$mmproj")
  if ($exe) { $set += "brainExe=$exe" }
  $null = Invoke-Klava $desk (@('--set') + $set)
  'Готово. В меню Клавы «Мозг» → «Локалка — Qwen3.6-35B-A3B uncensored».'
} else {
  "Модель на месте. Собери Клаву (УСТАНОВИТЬ.bat) — и выбери мозг «Локалка»."
}
