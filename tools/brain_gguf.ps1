# Перегнать мозг из полных весов (папка Hugging Face: safetensors + config) в GGUF для llama.cpp — то, на чём Клава
# говорит. Сначала полный bf16 GGUF во временную папку (на другом диске, если на этом тесно), потом квант (Q6_K: тот
# же размер и скорость, что у нынешнего мозга). Зрение (mmproj) у Qwen3.6-35B-A3B общее — берётся прежнее.
# Нужен llama.cpp с convert_hf_to_gguf.py и llama-quantize (сборка unsloth) и Python с torch/transformers (окружение
# большого голоса tts\.venv — в него ничего не ставится, gguf-py берётся из самого llama.cpp).
#
#   powershell -ExecutionPolicy Bypass -File tools\brain_gguf.ps1 -Src models\brain\qwen36-huihui -Name qwen36-huihui
#       [-Quant Q6_K] [-Tmp C:\KlavaTmp]
# bf16 остаётся во временной папке: из него делаются следующие кванты; удалять — руками.
param(
  [Parameter(Mandatory)] [string]$Src,
  [Parameter(Mandatory)] [string]$Name,
  [string]$Quant = 'Q6_K',
  [string]$Tmp = 'C:\KlavaTmp',
  [string]$LlamaDir = (Join-Path $env:USERPROFILE '.unsloth\llama.cpp')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathRooted($Src)) { $Src = Join-Path $root $Src }
$py = Join-Path $root 'tts\.venv\Scripts\python.exe'
$convert = Join-Path $LlamaDir 'convert_hf_to_gguf.py'
$quantize = Join-Path $LlamaDir 'build\bin\Release\llama-quantize.exe'
foreach ($f in $py, $convert, $quantize, (Join-Path $Src 'config.json'), (Join-Path $Src 'model.safetensors.index.json')) {
  if (-not (Test-Path $f)) { throw "нет $f" }
}
# все куски на месте — по списку из index.json
$index = Get-Content (Join-Path $Src 'model.safetensors.index.json') -Raw | ConvertFrom-Json
$shards = @($index.weight_map.PSObject.Properties.Value | Sort-Object -Unique)
$missing = @($shards | Where-Object { -not (Test-Path (Join-Path $Src $_)) })
if ($missing) { throw "не докачаны куски: $($missing -join ', ')" }
$srcGb = ($shards | ForEach-Object { (Get-Item (Join-Path $Src $_)).Length } | Measure-Object -Sum).Sum / 1GB

$outDir = Join-Path $root "models\brain\$Name"
New-Item -ItemType Directory -Force -Path $Tmp, $outDir | Out-Null
$bf16 = Join-Path $Tmp "$Name-BF16.gguf"
$final = Join-Path $outDir "$Name-$Quant.gguf"
$need = [long]($srcGb * 1.02 * 1GB)
$free = (Get-PSDrive (Split-Path $Tmp -Qualifier).TrimEnd(':')).Free
if (-not (Test-Path $bf16) -and $free -lt $need) { throw ("во временной папке {0} свободно {1:N0} ГБ, нужно {2:N0}" -f $Tmp, ($free / 1GB), ($need / 1GB)) }

if (Test-Path $bf16) { "есть bf16: $bf16" }
else {
  "перегоняю {0} ({1:N1} ГБ) → {2}  {3:HH:mm:ss}" -f $Src, $srcGb, $bf16, (Get-Date)
  & $py $convert $Src --outfile $bf16 --outtype bf16
  if ($LASTEXITCODE) { throw "convert_hf_to_gguf упал (код $LASTEXITCODE)" }
}
"квант {0} → {1}  {2:HH:mm:ss}" -f $Quant, $final, (Get-Date)
$threads = [Math]::Max(2, [Environment]::ProcessorCount / 2)
& $quantize $bf16 $final $Quant $threads
if ($LASTEXITCODE) { throw "llama-quantize упал (код $LASTEXITCODE)" }
"bf16 лежит тут (для следующих квантов; удалить — руками): $bf16"
"готово: {0} ({1:N1} ГБ)  {2:HH:mm:ss}" -f $final, ((Get-Item $final).Length / 1GB), (Get-Date)
