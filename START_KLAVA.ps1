# Запуск Клавы. Всё остальное — выбранный мозг (свой llama-server или облако), слух и голос — KlavaDesk поднимает
# сама и гасит при выходе. То же делает ярлык «Клава» на рабочем столе.
$desk = Join-Path $PSScriptRoot 'bin\KlavaDesk.exe'
if (-not (Test-Path $desk)) { Write-Host "Нет $desk — запусти УСТАНОВИТЬ.bat (соберёт Клаву и скачает голос)." -ForegroundColor Red; Read-Host 'Enter — выход'; exit 1 }
if (Get-Process KlavaDesk -ErrorAction SilentlyContinue) { Write-Host 'Клава уже на рабочем столе.'; Start-Sleep 2; exit 0 }
Start-Process -FilePath $desk -WorkingDirectory (Split-Path $desk)
