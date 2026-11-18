# Зависимости Клавы — общие функции для setup.ps1, setup_full.ps1, fetch_brain.ps1 и update.ps1 (подключается точкой: . deps.ps1).
# Работает в Windows PowerShell 5.1 (Windows 10) и PowerShell 7. Сначала пробует winget; если его нет (бывает на
# Windows 10 без «Установщика приложений») — качает официальный установщик напрямую и ставит без вопросов.

# PATH этого окна — заново из системы (только что поставленное winget'ом становится видно); переменные среды
# пользователя скрипты не меняют
function Refresh-Path {
  $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
}

function Has-Winget { [bool](Get-Command winget -ErrorAction SilentlyContinue) }

function Winget-Install([string]$Id) {
  if (-not (Has-Winget)) { return $false }
  & winget install --id $Id -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
  $code = $LASTEXITCODE
  Refresh-Path
  # 0 — поставлено; -1978335189 (0x8A15002B) — уже стоит и обновлять нечего
  return ($code -eq 0 -or $code -eq -1978335189)
}

# скачать файл: сначала в .part (оборвалось — следующий запуск докачает с места, а не примет огрызок за целый файл);
# curl.exe есть в Windows 10 с 1803, иначе — .NET
function Get-File([string]$Url, [string]$Out) {
  New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
  $part = "$Out.part"
  if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    for ($try = 1; $try -le 10; $try++) {
      & curl.exe -L --fail --silent --show-error --connect-timeout 30 --speed-limit 1024 --speed-time 60 -C - -o $part $Url
      $code = $LASTEXITCODE
      if ($code -eq 0 -and (Test-Path $part)) { Move-Item $part $Out -Force; return }
      # 33 — сервер не докачивает с этого места: начинаем заново
      if ($code -eq 33) { Remove-Item $part -ErrorAction SilentlyContinue }
      "  обрыв (curl код $code), попытка $try из 10…" | Out-Host
      Start-Sleep -Seconds ([math]::Min(3 * $try, 30))
    }
  }
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  (New-Object Net.WebClient).DownloadFile($Url, $part)
  Move-Item $part $Out -Force
}

# последний релиз на GitHub: ассет по маске имени
function Get-GithubAsset([string]$Repo, [string]$Like) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'klava-setup' }
  $rel.assets | Where-Object { $_.name -like $Like } | Select-Object -First 1
}

# KlavaDesk.exe — оконная программа: PowerShell её не ждёт и вывода не видит. Запуск с ожиданием, вывод — на экран,
# возвращает код выхода
function Invoke-Klava([string]$Desk, [string[]]$Arguments, [int]$Tail = 0) {
  $out = Join-Path $env:TEMP "klava_cli_$PID.out.txt"
  $err = Join-Path $env:TEMP "klava_cli_$PID.err.txt"
  $quoted = @($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' })
  $p = Start-Process -FilePath $Desk -ArgumentList $quoted -WorkingDirectory (Split-Path $Desk) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $out -RedirectStandardError $err
  $lines = @(Get-Content $out -Encoding UTF8 -ErrorAction SilentlyContinue) + @(Get-Content $err -Encoding UTF8 -ErrorAction SilentlyContinue)
  Remove-Item $out, $err -ErrorAction SilentlyContinue
  if ($Tail -gt 0) { $lines = @($lines | Select-Object -Last $Tail) }
  $lines | Where-Object { $_ } | ForEach-Object { "  $_" } | Out-Host
  return $p.ExitCode
}

# закрыть Клаву (и редактор аватара), запущенную из этой папки: сборка не может перезаписать занятый bin.
# Сначала по-хорошему (--quit: память записывается), через 30 с — силой
function Stop-Klava([string]$Root) {
  $mine = {
    param($p, [string]$dir)
    try { return $p.Path -and ($p.Path.StartsWith((Join-Path $Root $dir) + '\', [StringComparison]::OrdinalIgnoreCase)) } catch { return $false }
  }
  $desk = @(Get-Process KlavaDesk -ErrorAction SilentlyContinue | Where-Object { & $mine $_ 'bin' })
  $editor = @(Get-Process KlavaEditor -ErrorAction SilentlyContinue | Where-Object { & $mine $_ 'editor_bin' })
  if (-not $desk -and -not $editor) { '  Клава не запущена'; return }
  if ($desk) {
    '  закрываю Клаву (память записывается)…'
    try { Start-Process -FilePath $desk[0].Path -ArgumentList '--quit' -Wait } catch { }
    foreach ($p in $desk) { if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
  }
  foreach ($p in $editor) {
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(10000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  }
  Start-Sleep -Seconds 1
  '  закрыта'
}

function Assert-Windows {
  if (-not [Environment]::Is64BitOperatingSystem) { throw 'Нужна 64-битная Windows 10 или 11.' }
  $build = [Environment]::OSVersion.Version.Build
  # окно Клавы прячется от снимков экрана (WDA_EXCLUDEFROMCAPTURE) и собирается под Windows SDK 10.0.19041
  if ($build -lt 19041) { throw "Windows 10 сборки $build — слишком старая. Нужна Windows 10 версии 2004 (сборка 19041) или новее: Параметры → Обновление и безопасность → Центр обновления." }
  $name = if ($build -ge 22000) { 'Windows 11' } else { 'Windows 10' }
  "$name (сборка $build), ядер $([Environment]::ProcessorCount), ОЗУ {0:N0} ГБ" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
}

# .NET 9 SDK: где он есть — dotnet из PATH или стандартные папки (системная, пользовательская). Нашёлся не первым
# в PATH — эта папка ставится первой только в этом окне (сборка возьмёт её), системные переменные не трогаются
function Find-DotNet9 {
  $dirs = @()
  $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($cmd) { $dirs += Split-Path $cmd.Source }
  $dirs += (Join-Path $env:ProgramFiles 'dotnet'), (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet')
  foreach ($d in $dirs) {
    if ((Test-Path (Join-Path $d 'dotnet.exe')) -and (Get-ChildItem (Join-Path $d 'sdk') -Directory -Filter '9.*' -ErrorAction SilentlyContinue)) { return $d }
  }
  return $null
}

function Test-DotNet9 {
  $d = Find-DotNet9
  if (-not $d) { return $false }
  $first = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $first -or (Split-Path $first.Source) -ne $d) { $env:PATH = "$d;$env:PATH" }
  return $true
}

# рантайм окон .NET 9 в системной установке: его ищет сам KlavaDesk.exe при запуске (SDK в папке пользователя ему не виден)
function Test-DesktopRuntime9 {
  $loc = (Get-ItemProperty 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -ErrorAction SilentlyContinue).InstallLocation
  if (-not $loc) { $loc = Join-Path $env:ProgramFiles 'dotnet' }
  return [bool](Get-ChildItem (Join-Path $loc 'shared\Microsoft.WindowsDesktop.App') -Directory -Filter '9.*' -ErrorAction SilentlyContinue)
}

function Ensure-DotNet9 {
  Refresh-Path
  if ((Test-DotNet9) -and (Test-DesktopRuntime9)) { "  .NET 9 SDK: есть ($((Get-ChildItem (Join-Path (Find-DotNet9) 'sdk') -Directory -Filter '9.*' | Sort-Object Name | Select-Object -Last 1).Name))"; return }
  '  .NET 9 SDK нет — ставлю…'
  if (-not (Winget-Install 'Microsoft.DotNet.SDK.9') -or -not (Test-DotNet9) -or -not (Test-DesktopRuntime9)) {
    # без winget — официальный установщик Microsoft (последний 9.0), в Program Files; Windows спросит права администратора
    $exe = Join-Path $env:TEMP 'dotnet-sdk-9-win-x64.exe'
    Get-File 'https://aka.ms/dotnet/9.0/dotnet-sdk-win-x64.exe' $exe
    Start-Process $exe -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait
    Refresh-Path
  }
  if (-not (Test-DotNet9) -or -not (Test-DesktopRuntime9)) { throw '.NET 9 SDK не поставился — поставь вручную: https://dotnet.microsoft.com/download/dotnet/9.0 (SDK x64)' }
  '  .NET 9 SDK: поставлен'
}

function Test-WebView2 {
  $keys = @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
            'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
            'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')
  foreach ($k in $keys) { $pv = (Get-ItemProperty $k -ErrorAction SilentlyContinue).pv; if ($pv -and $pv -ne '0.0.0.0') { return $true } }
  return $false
}

function Ensure-WebView2 {
  if (Test-WebView2) { '  WebView2: есть'; return }
  '  WebView2 Runtime нет (на Windows 10 бывает) — ставлю…'
  if (-not (Winget-Install 'Microsoft.EdgeWebView2Runtime') -or -not (Test-WebView2)) {
    $exe = Join-Path $env:TEMP 'MicrosoftEdgeWebview2Setup.exe'
    Get-File 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' $exe
    Start-Process $exe -ArgumentList '/silent', '/install' -Wait
  }
  if (Test-WebView2) { '  WebView2: поставлен' } else { throw 'WebView2 не поставился — поставь вручную: https://developer.microsoft.com/microsoft-edge/webview2 (Evergreen Bootstrapper)' }
}

function Test-VCRedist {
  $k = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
  if (-not $k) { $k = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue }
  return ($k -and $k.Installed -eq 1 -and $k.Major -ge 14 -and $k.Minor -ge 30)
}

# Visual C++ 2015–2022 x64: без него не грузятся слух и голос на процессоре (sherpa-onnx / onnxruntime) и llama.cpp
function Ensure-VCRedist {
  if (Test-VCRedist) { '  Visual C++ рантайм: есть'; return }
  '  Visual C++ 2015–2022 x64 нет — ставлю (Windows может спросить разрешение администратора)…'
  if (-not (Winget-Install 'Microsoft.VCRedist.2015+.x64') -or -not (Test-VCRedist)) {
    $exe = Join-Path $env:TEMP 'vc_redist.x64.exe'
    Get-File 'https://aka.ms/vs/17/release/vc_redist.x64.exe' $exe
    Start-Process $exe -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait
  }
  if (Test-VCRedist) { '  Visual C++ рантайм: поставлен' } else { '  Visual C++ рантайм не встал — если лёгкий голос не заговорит, поставь: https://aka.ms/vs/17/release/vc_redist.x64.exe' }
}

# Распознавание русского текста Windows (Windows.Media.Ocr): им Клава читает надписи в играх и на экране. На Windows
# с английским языком интерфейса его нет — ставится компонентом «Language.OCR ru-RU» (нужны права администратора)
function Test-OcrRu {
  try {
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    return [bool]([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | Where-Object { $_.LanguageTag -like 'ru*' })
  } catch { return $true }   # PowerShell без WinRT — проверить нечем, не трогаем
}

function Ensure-OcrRu {
  if (Test-OcrRu) { '  распознавание русского текста: есть'; return }
  '  русского распознавания текста в Windows нет (не прочтёт русские надписи в играх) — ставлю, Windows спросит права администратора…'
  try {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', "Add-WindowsCapability -Online -Name 'Language.OCR~~~ru-RU~0.0.1.0'"
  } catch { '  отказано в правах — пропускаю' }
  if (Test-OcrRu) { '  распознавание русского текста: поставлено' }
  else { '  не встало — поставь сам: Параметры → Время и язык → Язык → Добавить русский (хватит «Распознавания текста»)' }
}

# llama.cpp (llama-server): свой мозг и векторы памяти (bge-m3). Сборка по видеокарте: NVIDIA → CUDA 12.4,
# AMD/Intel → Vulkan, иначе процессор. Уже стоит — не трогается (-Force — перекачать свежий)
function Ensure-Llama([string]$Root, [switch]$Force) {
  $dest = Join-Path $Root 'runtime\llama'
  if (-not $Force -and (Test-Path (Join-Path $dest 'llama-server.exe'))) { '  llama.cpp: есть'; return }
  $gpu = (Get-CimInstance Win32_VideoController | ForEach-Object Name) -join ' | '
  $kind = if ($gpu -match 'NVIDIA') { 'cuda-12.4' } elseif ($gpu -match 'AMD|Radeon|Intel\(R\) Arc|Intel\(R\) (Iris|UHD)') { 'vulkan' } else { 'cpu' }
  "  видеокарта: $gpu → llama.cpp $kind"
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $rel = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5' -Headers @{ 'User-Agent' = 'klava-setup' } |
    Where-Object { $_.assets | Where-Object name -like "*bin-win-$kind-x64.zip" } | Select-Object -First 1
  if (-not $rel) { throw "в релизах llama.cpp нет сборки win-$kind-x64" }
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  $zips = @($rel.assets | Where-Object { $_.name -like "llama-*-bin-win-$kind-x64.zip" })
  if ($kind -like 'cuda*') { $zips += $rel.assets | Where-Object { $_.name -like "cudart-llama-bin-win-$kind-x64.zip" } }
  foreach ($z in $zips) {
    $zip = Join-Path $env:TEMP $z.name
    "  качаю $($z.name) ({0:N0} МБ)…" -f ($z.size / 1MB)
    Get-File $z.browser_download_url $zip
    Expand-Archive -Path $zip -DestinationPath $dest -Force
    Remove-Item $zip
  }
  # в архивах бывает вложенная папка — llama-server.exe нужен прямо в runtime\llama
  $srv = Get-ChildItem $dest -Recurse -Filter llama-server.exe | Select-Object -First 1
  if (-not $srv) { throw 'в архиве llama.cpp нет llama-server.exe' }
  if ($srv.DirectoryName -ne $dest) { Get-ChildItem $srv.DirectoryName | Move-Item -Destination $dest -Force }
  "  llama.cpp $($rel.tag_name) ($kind) → $dest"
}

# git: нужен большому голосу (Seed-VC, ACE-Step) и обновлению из GitHub. Без winget — MinGit в runtime\git (без установки)
function Ensure-Git([string]$Root) {
  Refresh-Path
  if (Get-Command git -ErrorAction SilentlyContinue) { return }
  $local = Join-Path $Root 'runtime\git\cmd'
  if (Test-Path (Join-Path $local 'git.exe')) { $env:PATH = "$local;$env:PATH"; return }
  '  git нет — ставлю…'
  if ((Winget-Install 'Git.Git') -and (Get-Command git -ErrorAction SilentlyContinue)) { return }
  $asset = Get-GithubAsset 'git-for-windows/git' 'MinGit-*-64-bit.zip'
  if (-not $asset) { throw 'не нашла MinGit в релизах git-for-windows' }
  $zip = Join-Path $env:TEMP $asset.name
  Get-File $asset.browser_download_url $zip
  Expand-Archive -Path $zip -DestinationPath (Join-Path $Root 'runtime\git') -Force
  Remove-Item $zip
  $env:PATH = "$local;$env:PATH"
  "  git: MinGit $($asset.name) → runtime\git"
}

# Python 3.12 — только для большого голоса (видеокарта NVIDIA)
function Ensure-Python312 {
  try { $v = & py -3.12 --version 2>$null; if ($LASTEXITCODE -eq 0) { "  Python: $v"; return } } catch { }
  '  Python 3.12 нет — ставлю…'
  if (-not (Winget-Install 'Python.Python.3.12')) {
    $exe = Join-Path $env:TEMP 'python-3.12.10-amd64.exe'
    Get-File 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe' $exe
    Start-Process $exe -ArgumentList '/quiet', 'InstallAllUsers=0', 'PrependPath=1', 'Include_launcher=1', 'Include_test=0' -Wait
  }
  Refresh-Path
  try { $v = & py -3.12 --version 2>$null } catch { $v = $null }
  if (-not $v) { throw 'Python 3.12 не поставился — поставь вручную с python.org (галочка «Add to PATH»)' }
  "  Python: $v"
}

# Blender — мастерской Клавы для сложных вещей (по желанию)
function Find-Blender {
  $base = Join-Path $env:ProgramFiles 'Blender Foundation'
  if (-not (Test-Path $base)) { return $null }
  Get-ChildItem $base -Directory -Filter 'Blender*' | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'blender.exe' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
