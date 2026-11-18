# Несжатые веса обычного Qwen3.6-35B-A3B (Apache-2.0, официальный репозиторий Qwen) — основа для обучения экспертов
# (mind\): 26 кусков safetensors, 67 ГБ. Качает кусками с докачкой и сверкой SHA-256 (tools\hf_get.ps1);
# прервали — запусти ещё раз, готовые куски пропустит.
#   powershell -ExecutionPolicy Bypass -File tools\fetch_base_brain.ps1 [-OutDir H:\Neiro\Neko\models\brain\qwen36-base]
param(
  [string]$Repo = 'Qwen/Qwen3.6-35B-A3B',
  [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'models\brain\qwen36-base')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$tree = Invoke-RestMethod "https://huggingface.co/api/models/$Repo/tree/main?recursive=true"
# картинки-видео этому стенду не нужны, только текстовая часть и её настройки
$files = $tree | Where-Object { $_.type -eq 'file' -and $_.path -notmatch '^(README\.md|LICENSE|\.gitattributes)$' } | Sort-Object { if ($_.lfs) { $_.lfs.size } else { $_.size } }
$total = ($files | ForEach-Object { if ($_.lfs) { $_.lfs.size } else { $_.size } } | Measure-Object -Sum).Sum
"{0}: файлов {1}, всего {2:N1} ГБ -> {3}" -f $Repo, $files.Count, ($total / 1GB), $OutDir
$n = 0
foreach ($f in $files) {
  $n++
  $size = [long]($(if ($f.lfs) { $f.lfs.size } else { $f.size }))
  $dest = Join-Path $OutDir $f.path
  if ((Test-Path $dest) -and (Get-Item $dest).Length -eq $size) { "[{0}/{1}] есть: {2}" -f $n, $files.Count, $f.path; continue }
  "[{0}/{1}] {2} ({3:N2} ГБ) {4:HH:mm:ss}" -f $n, $files.Count, $f.path, ($size / 1GB), (Get-Date)
  & (Join-Path $PSScriptRoot 'hf_get.ps1') -Repo $Repo -File $f.path -OutDir $OutDir
  if ($LASTEXITCODE -ne 0 -and -not ((Test-Path $dest) -and (Get-Item $dest).Length -eq $size)) { throw "не скачался $($f.path)" }
}
"готово: {0:HH:mm:ss}" -f (Get-Date)
