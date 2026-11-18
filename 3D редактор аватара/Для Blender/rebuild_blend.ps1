# Собирает «Клава.blend» из «Клава.glb» (свежий GLB даёт кнопка «Экспорт для Blender» в Редакторе Клавы).
$ErrorActionPreference = 'Stop'
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
if (-not (Test-Path $blender)) {
    $found = Get-ChildItem 'C:\Program Files\Blender Foundation' -Filter blender.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { Write-Host 'Не нашёл Blender — поправь путь в rebuild_blend.ps1' -ForegroundColor Red; Read-Host 'Enter — закрыть'; exit 1 }
    $blender = $found.FullName
}
Write-Host "Собираю Клава.blend через $blender …" -ForegroundColor Cyan
& $blender -b --python (Join-Path $PSScriptRoot 'build_blend.py')
if ($LASTEXITCODE -ne 0) { Write-Host 'Сборка упала — смотри текст выше.' -ForegroundColor Red; Read-Host 'Enter — закрыть'; exit 1 }
Write-Host "Готово: $(Join-Path $PSScriptRoot 'Клава.blend')" -ForegroundColor Green
Start-Sleep -Seconds 4
