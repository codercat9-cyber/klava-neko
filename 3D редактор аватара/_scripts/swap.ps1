# Примерить модель на живую Клаву. Без аргумента — Unskined.blend из папки «3D редактор аватара».
# Перед первой примеркой оригинал Клавы сохраняется (config.backups\ОРИГИНАЛ_config.json + копия в «Модель\Оригинал Клавы (бэкап)»).
param([string]$Model)
$exe = 'H:\Neiro\Neko\editor_bin\KlavaEditor.exe'
$log = 'H:\Neiro\Neko\data\logs\swap.log'
if (-not $Model) { $Model = Join-Path (Split-Path $PSScriptRoot -Parent) 'Unskined.blend' }
Write-Host "Примеряю на Клаву: $Model" -ForegroundColor Cyan
if ($Model -like '*.blend') { Write-Host 'Blender выгружает .blend в VRM — это секунд 30–60…' }
$p = Start-Process -FilePath $exe -ArgumentList "--swap `"$Model`"" -Wait -PassThru
if (Test-Path $log) { Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host $_ } }
if ($p.ExitCode -eq 0) { Write-Host 'Готово. Вернуть: «Вернуть оригинальную Клаву».' -ForegroundColor Green; Start-Sleep -Seconds 6 }
else { Write-Host "Не вышло (код $($p.ExitCode)) — текст выше." -ForegroundColor Red; Read-Host 'Enter — закрыть' }
