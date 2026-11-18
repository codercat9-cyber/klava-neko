# Вернуть оригинальную Клаву после примерки (конфиг из config.backups\ОРИГИНАЛ_config.json).
$exe = 'H:\Neiro\Neko\editor_bin\KlavaEditor.exe'
$log = 'H:\Neiro\Neko\data\logs\swap.log'
$p = Start-Process -FilePath $exe -ArgumentList '--restore' -Wait -PassThru
if (Test-Path $log) { Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host $_ } }
if ($p.ExitCode -eq 0) { Write-Host 'Готово.' -ForegroundColor Green; Start-Sleep -Seconds 4 }
else { Write-Host "Не вышло (код $($p.ExitCode))." -ForegroundColor Red; Read-Host 'Enter — закрыть' }
