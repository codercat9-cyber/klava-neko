# Собрать Клаву из исходников: Klava.sln (KlavaDesk → bin, редактор аватара → editor_bin) — то же, что шаг «Сборка»
# УСТАНОВИТЬ.bat, только без скачиваний и установок. Запущенную из этой папки Клаву закрывает по-хорошему (--quit:
# память записывается), после удачной сборки запускает снова. Сборка упала — bin не тронут, запускает прежнюю.
# SDK — .NET 9 (global.json в корне: превью 10 не берётся, даже если стоит). Нет SDK 9 — запусти УСТАНОВИТЬ.bat.
#
#   СОБРАТЬ_КЛАВУ.bat                       — Release, выложить в bin и editor_bin, запустить, если была запущена
#   powershell -File tools\build.ps1 -Test  — ещё и самопроверки (--selftest, --presence-test) перед запуском
#   powershell -File tools\build.ps1 -Debug — отладочная сборка в app\*\bin\Debug (живую Клаву не трогает)
#   -NoStart — не запускать после сборки; -Start — запустить, даже если не была запущена
param([switch]$Test, [switch]$Debug, [switch]$NoStart, [switch]$Start)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'deps.ps1')
Refresh-Path
if (-not (Test-DotNet9)) {
  Write-Host 'Нет .NET 9 SDK — запусти УСТАНОВИТЬ.bat (он поставит всё нужное).' -ForegroundColor Red
  exit 1
}
$sln = Join-Path $root 'Klava.sln'
$bin = Join-Path $root 'bin'
$desk = Join-Path $bin 'KlavaDesk.exe'
$cfg = if ($Debug) { 'Debug' } else { 'Release' }

# Release выкладывается в bin — запущенную оттуда Клаву (и редактор из editor_bin) сначала закрыть
$wasRunning = $false
if (-not $Debug) {
  $wasRunning = [bool](Get-Process KlavaDesk -ErrorAction SilentlyContinue | Where-Object {
      try { $_.Path -and $_.Path.StartsWith($bin + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
  Stop-Klava $root
}

''
"=== Сборка ($cfg) ==="
$t = [Diagnostics.Stopwatch]::StartNew()
& dotnet build $sln -c $cfg --nologo -v q
$ok = $LASTEXITCODE -eq 0
if ($ok) { "  собрано за $([int]$t.Elapsed.TotalSeconds) с" }
else { Write-Host '  НЕ СОБРАЛОСЬ — ошибки выше; bin не тронут' -ForegroundColor Red }

# прогон набора проверок KlavaDesk (WinExe: вывод только через перенаправление): провалы «[ПЛОХО]» и итог
function Invoke-Suite([string]$Exe, [string]$Suite) {
  $out = Join-Path $env:TEMP "klava_build_$PID.txt"
  $p = Start-Process -FilePath $Exe -ArgumentList $Suite -WorkingDirectory (Split-Path $Exe) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $out
  $lines = @(Get-Content $out -Encoding UTF8 -ErrorAction SilentlyContinue)
  Remove-Item $out -ErrorAction SilentlyContinue
  @($lines | Where-Object { $_ -match '\[ПЛОХО\]' }) + @($lines | Where-Object { $_ } | Select-Object -Last 2) |
    ForEach-Object { "    $_" } | Out-Host
  return $p.ExitCode
}

if ($ok -and $Test) {
  ''
  '=== Самопроверки ==='
  $exe = if ($Debug) {
    Get-ChildItem (Join-Path $root 'app\KlavaDesk\bin\Debug') -Recurse -Filter KlavaDesk.exe | Select-Object -First 1 -ExpandProperty FullName
  } else { $desk }
  foreach ($suite in '--selftest', '--presence-test') {
    "  $suite"
    if ((Invoke-Suite $exe $suite) -ne 0) { $ok = $false; Write-Host "  ${suite}: ЕСТЬ ПРОВАЛЫ" -ForegroundColor Red }
  }
}

if (-not $Debug -and -not $NoStart -and ($wasRunning -or $Start) -and (Test-Path $desk)) {
  ''
  if ($ok) { 'запускаю Клаву' } else { 'запускаю прежнюю Клаву (новая не собралась)' }
  Start-Process -FilePath $desk -WorkingDirectory $bin
}
elseif (-not $Debug -and $ok -and -not $NoStart) { ''; 'готово; запуск — ярлык «Клава» или START_KLAVA.bat' }
if ($ok) { exit 0 } else { exit 1 }
