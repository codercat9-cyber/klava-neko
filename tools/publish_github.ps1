# Отправить папку проекта в ПРИВАТНЫЙ репозиторий GitHub. Первый запуск: поставит git и GitHub CLI (winget),
# откроет браузер для входа, создаст репозиторий. Дальше — просто коммит и push изменений.
#
#   powershell -ExecutionPolicy Bypass -File tools\publish_github.ps1 [-Dir папка] [-Repo klava-neko]
param([string]$Dir = (Split-Path $PSScriptRoot -Parent), [string]$Repo = 'klava-neko')
$ErrorActionPreference = 'Stop'
Set-Location $Dir
function Refresh-Path { $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User') }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  'Нет git — ставлю через winget (Git.Git)…'
  & winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements
  Refresh-Path
}
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not $gh) {
  'Нет GitHub CLI — ставлю через winget (GitHub.cli)…'
  & winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements
  Refresh-Path
  $gh = if (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe") { "$env:ProgramFiles\GitHub CLI\gh.exe" } else { (Get-Command gh).Source }
}

& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
  'Вход в GitHub: сейчас откроется браузер — вставь код, который покажется здесь, и разреши доступ.'
  & $gh auth login --hostname github.com --git-protocol https --web
  if ($LASTEXITCODE -ne 0) { throw 'вход в GitHub не удался' }
}
& $gh auth setup-git | Out-Null
$login = (& $gh api user --jq .login).Trim()
$id = (& $gh api user --jq .id).Trim()
"GitHub: $login"

if (-not (Test-Path (Join-Path $Dir '.git'))) { & git init -b main | Out-Host }
# подпись коммитов — в этом репозитории, глобальные настройки git не трогаем
if (-not (& git config user.name)) { & git config user.name $login }
if (-not (& git config user.email)) { & git config user.email "$id+$login@users.noreply.github.com" }

$big = & git ls-files -o -c --exclude-standard | Where-Object { (Test-Path $_) -and (Get-Item $_).Length -gt 95MB }
if ($big) { throw "файлы больше 95 МБ (GitHub не примет): $($big -join ', ')" }
& git add -A
if (& git status --porcelain) {
  & git commit -q -m "Клава: обновление $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -m 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'
  if ($LASTEXITCODE -ne 0) { throw 'коммит не создался' }
  & git log --oneline -1 | Out-Host
} else { 'изменений нет — отправляю то, что есть' }

$remote = & git remote get-url origin 2>$null
if (-not $remote) {
  & $gh repo view "$login/$Repo" *> $null
  if ($LASTEXITCODE -eq 0) {
    & git remote add origin "https://github.com/$login/$Repo.git"
    & git push -u origin main
  } else {
    & $gh repo create $Repo --private --source . --remote origin --push --description 'Клава — ИИ-кошкодевочка на рабочем столе'
  }
} else {
  & git push -u origin main
}
if ($LASTEXITCODE -ne 0) { throw 'push не прошёл' }
''
"Готово: https://github.com/$login/$Repo (приватный)"
