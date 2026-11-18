# Залить пакет Клавы на GitHub по токену — без GitHub CLI и без входа через браузер.
# Спрашивает токен (скрытый ввод), имя репозитория или ссылку на него, сам создаёт репозиторий, если его нет, коммитит
# и пушит. Токен нигде не сохраняется: ни в git config, ни в адресе remote, ни в файлах — он живёт только в памяти
# этого окна и уходит в git через переменные окружения на время одного push.
#
#   powershell -ExecutionPolicy Bypass -File tools\push_github.ps1 [-Dir папка] [-Repo имя|владелец/имя|ссылка] [-Public] [-Force]
#
# Токен: github.com → Settings → Developer settings → Personal access tokens:
#   classic — галка «repo»; fine-grained — Repository access на нужный репозиторий (или All) и Contents: Read and write,
#   а чтобы скрипт сам создавал репозиторий — Administration: Read and write.
param(
  [string]$Dir = (Split-Path $PSScriptRoot -Parent),
  [string]$Repo = '',
  [switch]$Public,
  [switch]$Force,
  [string]$Branch = 'main'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Dir = (Resolve-Path $Dir).Path.TrimEnd('\')
Set-Location $Dir

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Fail([string]$text) { Say "✗ $text" 'Red'; exit 1 }

Say "Пакет: $Dir" 'Cyan'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  Fail 'нет git. Поставь: winget install --id Git.Git -e   (или https://git-scm.com) — и запусти снова.'
}
if (-not (Test-Path (Join-Path $Dir 'START_KLAVA.ps1')) -and -not (Test-Path (Join-Path $Dir 'app'))) {
  Fail "в $Dir не похоже на пакет Клавы (нет START_KLAVA.ps1 и app) — собери пакет в пульте («Собрать пакет»)."
}

# ---------------------------------------------------------------- токен
$token = $null
if ($env:GITHUB_TOKEN) {
  $use = Read-Host 'Нашла GITHUB_TOKEN в окружении — взять его? (Enter — да, n — ввести другой)'
  if ($use -notmatch '^(n|н|no|нет)$') { $token = $env:GITHUB_TOKEN }
}
if (-not $token) {
  $sec = Read-Host 'Токен GitHub (ввод скрыт)' -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
$token = "$token".Trim()
if (-not $token) { Fail 'токен пустой.' }

$api = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'klava-push' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { $me = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $api -TimeoutSec 30 }
catch { Fail "GitHub не принял токен: $($_.Exception.Message)" }
Say "GitHub: вошла как $($me.login)" 'Green'

# ---------------------------------------------------------------- репозиторий
if (-not $Repo) { $Repo = Read-Host 'Имя репозитория, владелец/имя или ссылка (Enter — klava-neko)' }
$Repo = "$Repo".Trim()
if (-not $Repo) { $Repo = 'klava-neko' }
$owner = $me.login
$name = $Repo
if ($Repo -match 'github\.com[/:]([^/]+)/([^/?#]+?)(?:\.git)?/?$') { $owner = $Matches[1]; $name = $Matches[2] }
elseif ($Repo -match '^([^/\s]+)/([^/\s]+)$') { $owner = $Matches[1]; $name = $Matches[2] -replace '\.git$', '' }
if ($name -notmatch '^[A-Za-z0-9._-]+$') { Fail "имя репозитория «$name» — только латиница, цифры, точка, дефис и подчёркивание." }
$web = "https://github.com/$owner/$name"

$exists = $true
try { $info = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$name" -Headers $api -TimeoutSec 30 }
catch {
  $code = try { [int]$_.Exception.Response.StatusCode } catch { 0 }
  if ($code -ne 404) { Fail "не смогла проверить репозиторий $owner/$name ($($_.Exception.Message))" }
  $exists = $false
}
if ($exists) { Say "Репозиторий есть: $web ($(if ($info.private) { 'приватный' } else { 'публичный' }))" 'Green' }
else {
  $private = -not $Public
  if (-not $Public) {
    $ans = Read-Host "Репозитория $owner/$name нет — создать приватным? (Enter — да, p — публичным)"
    if ($ans -match '^(p|п|public|публ)') { $private = $false }
  }
  $body = @{ name = $name; private = $private; description = 'Клава — ИИ-кошкодевочка на рабочем столе'; has_wiki = $false } | ConvertTo-Json
  $url = if ($owner -ieq $me.login) { 'https://api.github.com/user/repos' } else { "https://api.github.com/orgs/$owner/repos" }
  try { $null = Invoke-RestMethod -Uri $url -Method Post -Headers $api -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json; charset=utf-8' -TimeoutSec 30 }
  catch { Fail "не создался репозиторий $owner/$name ($($_.Exception.Message)). У fine-grained токена нужно право Administration: write." }
  Say "Создан $(if ($private) { 'приватный' } else { 'публичный' }) репозиторий: $web" 'Green'
}

# ---------------------------------------------------------------- проверка на утечки перед коммитом
# в пакете не должно быть личного (pack_github это вырезает) — но лучше лишний раз глянуть глазами скрипта
$personal = @('klava.json', 'data', 'web\config.json') | Where-Object { Test-Path (Join-Path $Dir $_) }
if ($personal) { Fail "в пакете лежит личное: $($personal -join ', ') — собери пакет заново через пульт (pack_github его вырезает)." }
$patterns = 'sk-[A-Za-z0-9]{32,}', 'sk-ant-[A-Za-z0-9_-]{20,}', 'AIza[0-9A-Za-z_-]{35}', 'ghp_[A-Za-z0-9]{36}', 'github_pat_[A-Za-z0-9_]{40,}', 'xox[bp]-[A-Za-z0-9-]{20,}'
$textExt = '.cs', '.ps1', '.bat', '.json', '.md', '.txt', '.py', '.js', '.mjs', '.html', '.css', '.toml', '.yaml', '.yml', '.env', '.ini', '.xml', '.csproj', '.props', '.targets'
$leaks = Get-ChildItem $Dir -Recurse -File -Force -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\(\.git|node_modules)\\' -and ($textExt -contains $_.Extension.ToLowerInvariant()) -and $_.Length -lt 2MB } |
  Select-String -Pattern $patterns -List -ErrorAction SilentlyContinue
if ($leaks) {
  Say 'Похоже на ключи в файлах — пуш остановлен:' 'Red'
  $leaks | ForEach-Object { Say "  $($_.Path.Substring($Dir.Length + 1)):$($_.LineNumber)" 'Red' }
  exit 1
}
$big = Get-ChildItem $Dir -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\\.git\\' -and $_.Length -gt 95MB }
if ($big) { Fail "файлы больше 95 МБ (GitHub не примет): $(($big | ForEach-Object { $_.FullName.Substring($Dir.Length + 1) }) -join ', ')" }

# ---------------------------------------------------------------- git
if (-not (Test-Path (Join-Path $Dir '.git'))) { & git init -b $Branch | Out-Host }
# подпись коммитов — только в этом репозитории, глобальный git не трогаем
if (-not (& git config user.name)) { & git config user.name $me.login }
if (-not (& git config user.email)) { & git config user.email "$($me.id)+$($me.login)@users.noreply.github.com" }
& git config core.autocrlf false
& git config core.quotepath false
# большой пуш: HTTP/1.1 и буфер побольше — 18.11 первый пуш (164 МиБ одним пакетом, через прокси) GitHub оборвал:
# «HTTP 408», «remote end hung up unexpectedly». Поэтому ещё и частями (ниже)
& git config http.version HTTP/1.1
& git config http.postBuffer 524288000
$remoteUrl = "$web.git"
$hasOrigin = (& git remote) -contains 'origin'
if ($hasOrigin) { & git remote set-url origin $remoteUrl } else { & git remote add origin $remoteUrl }

# токен — в заголовок для github.com через переменные окружения git (не в командную строку, не в config, не в URL)
$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token"))
$env:GIT_CONFIG_COUNT = '1'
$env:GIT_CONFIG_KEY_0 = 'http.https://github.com/.extraheader'
$env:GIT_CONFIG_VALUE_0 = "AUTHORIZATION: basic $basic"
$env:GIT_TERMINAL_PROMPT = '0'

# credential.helper= — пустой список помощников: менеджер учёток Windows не спросит и ничего не запомнит
# (пустое значение — через -c: в Windows PowerShell присвоение '' переменной окружения её удаляет)
$script:forceLeft = [bool]$Force
function Push-Head([string]$what) {
  for ($try = 1; $try -le 3; $try++) {
    # -Force — именно --force (и только первым пушем): --force-with-lease без скачанной ветки origin отказывает
    $pushArgs = @('-c', 'credential.helper=', 'push', '-u', 'origin', "HEAD:refs/heads/$Branch")
    if ($script:forceLeft) { $pushArgs = @('-c', 'credential.helper=', 'push', '--force', '-u', 'origin', "HEAD:refs/heads/$Branch") }
    & git @pushArgs
    if ($LASTEXITCODE -eq 0) { $script:forceLeft = $false; return $true }
    if ($try -lt 3) { Say "  обрыв на «$what» (попытка $try из 3) — ещё раз" 'Yellow'; Start-Sleep -Seconds 3 }
  }
  return $false
}

$ok = $false
try {
  # прошлый первый пуш оборвался, а на GitHub ветки так и нет — тот большой коммит переделываем частями (терять нечего)
  $remoteHead = & git -c credential.helper= ls-remote --heads origin $Branch
  if (-not $remoteHead -and (& git rev-parse --verify -q HEAD)) {
    Say 'На GitHub ветки ещё нет, а локально есть незалитый коммит — переделываю его частями.' 'Yellow'
    & git update-ref -d HEAD
  }
  $unborn = -not (& git rev-parse --verify -q HEAD)

  & git add -A
  $changed = @((& git diff --cached --name-only -z) -join '' -split "`0" | Where-Object { $_ })
  if ($changed.Count -eq 0) {
    if ($unborn) { Fail 'нечего коммитить — пакет пустой?' }
    Say 'Изменений нет — пушу то, что уже закоммичено.' 'Gray'
    $ok = Push-Head 'всё'
  }
  else {
    $sizeOf = @{}
    foreach ($p in $changed) { $f = Join-Path $Dir $p; $sizeOf[$p] = if (Test-Path -LiteralPath $f) { (Get-Item -LiteralPath $f).Length } else { 0 } }
    $total = ($sizeOf.Values | Measure-Object -Sum).Sum
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
    if ($total -le 60MB) {
      & git commit -q -m "Клава: обновление $stamp"
      if ($LASTEXITCODE -ne 0) { Fail 'коммит не удался' }
      Say "Коммит: $(& git log -1 --format='%h %s')" 'Green'
      Say "Пушу в $web ($Branch)…" 'Cyan'
      $ok = Push-Head 'всё'
    }
    else {
      # частями по ~45 МБ: отдельный коммит и пуш на часть — маленький пакет GitHub по таймауту не рвёт
      $batches = @(); $cur = @(); $curSize = 0
      foreach ($p in ($changed | Sort-Object)) {
        if ($cur.Count -gt 0 -and $curSize + $sizeOf[$p] -gt 45MB) { $batches += , $cur; $cur = @(); $curSize = 0 }
        $cur += $p; $curSize += $sizeOf[$p]
      }
      if ($cur.Count -gt 0) { $batches += , $cur }
      Say ("Изменений {0:N0} МБ — заливаю частями: {1}" -f ($total / 1MB), $batches.Count) 'Cyan'
      if ($unborn) { & git read-tree --empty } else { & git reset -q }
      $list = Join-Path ([IO.Path]::GetTempPath()) "klava_push_$PID.txt"
      $ok = $true
      for ($i = 0; $i -lt $batches.Count; $i++) {
        [IO.File]::WriteAllText($list, (($batches[$i] -join "`0") + "`0"), (New-Object Text.UTF8Encoding $false))
        & git --literal-pathspecs add -A --pathspec-from-file=$list --pathspec-file-nul
        & git commit -q -m "Клава: обновление $stamp (часть $($i + 1) из $($batches.Count))"
        if ($LASTEXITCODE -ne 0) { $ok = $false; Say "коммит части $($i + 1) не удался" 'Red'; break }
        Say ("Часть {0} из {1}: файлов {2}" -f ($i + 1), $batches.Count, $batches[$i].Count) 'Cyan'
        if (-not (Push-Head "часть $($i + 1)")) { $ok = $false; break }
      }
      Remove-Item $list -ErrorAction SilentlyContinue
    }
  }
}
finally {
  foreach ($v in 'GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0', 'GIT_TERMINAL_PROMPT') {
    Remove-Item "Env:$v" -ErrorAction SilentlyContinue
  }
  $token = $null; $basic = $null; $api = $null
}
if (-not $ok) {
  Say '✗ push не прошёл.' 'Red'
  Say '  «rejected / fetch first» — в репозитории уже есть чужие коммиты: запусти с -Force (перезапишет их) или выбери другое имя.' 'Yellow'
  Say '  «403» — у токена нет права на запись (classic: repo; fine-grained: Contents: Read and write на этот репозиторий).' 'Yellow'
  exit 1
}
Say "✓ Готово: $web" 'Green'
exit 0
