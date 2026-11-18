# Лёгкий комплект слуха и голоса Клавы (~350 МБ): работает на процессоре, без Python и видеокарты.
#   слух      GigaAM v3 (русский, с пунктуацией)              ~160 МБ
#   голос     Supertonic-3 (русский, 10 голосов)              ~130 МБ
#   запасной  Piper «Ирина» (русский женский)                  ~20 МБ
#   паузы     Silero VAD                                       ~1 МБ
#   профили   ERes2Net (3D-Speaker) — кто из друзей говорит   ~25 МБ
# Всё с релизов sherpa-onnx на GitHub. Уже скачанное пропускается; оборвалось — запусти ещё раз, докачает.
#
#   powershell -ExecutionPolicy Bypass -File tools\fetch_lite.ps1
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Root 'models\lite'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$base = 'https://github.com/k2-fsa/sherpa-onnx/releases/download'

# что качать: адрес, во что превращается (папка после распаковки или сам файл)
$items = @(
  @{ Url = "$base/asr-models/sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16.tar.bz2"; Result = 'sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16' },
  @{ Url = "$base/tts-models/sherpa-onnx-supertonic-3-tts-int8-2026-05-11.tar.bz2"; Result = 'sherpa-onnx-supertonic-3-tts-int8-2026-05-11' },
  @{ Url = "$base/tts-models/vits-piper-ru_RU-irina-medium-int8.tar.bz2"; Result = 'vits-piper-ru_RU-irina-medium-int8' },
  @{ Url = "$base/asr-models/silero_vad.onnx"; Result = 'silero_vad.onnx' },
  @{ Url = "$base/speaker-recongition-models/3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx"; Result = '3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx' }
)

function Get-File([string]$url, [string]$out) {
  # curl.exe есть в Windows 10/11; -C - докачивает с места обрыва, цикл — на случай обрыва связи
  for ($try = 1; $try -le 20; $try++) {
    & curl.exe -L --fail --silent --show-error --connect-timeout 30 --speed-limit 1024 --speed-time 60 -C - -o $out $url
    if ($LASTEXITCODE -eq 0) { return }
    # 33 — сервер не умеет докачку с этого места: файл уже целый или битый, начинаем заново
    if ($LASTEXITCODE -eq 33) { Remove-Item $out -ErrorAction SilentlyContinue }
    "  обрыв (curl код $LASTEXITCODE), попытка $try из 20…"
    Start-Sleep -Seconds ([math]::Min(3 * $try, 30))
  }
  throw "не скачалось: $url"
}

foreach ($it in $items) {
  $target = Join-Path $dir $it.Result
  if (Test-Path $target) { "есть: $($it.Result)"; continue }
  $name = Split-Path $it.Url -Leaf
  $file = Join-Path $dir $name
  "качаю $name…"
  Get-File $it.Url $file
  if ($name.EndsWith('.tar.bz2')) {
    "  распаковываю…"
    # именно системный tar (bsdtar умеет bz2 сам); tar из Git в PATH без bzip2 падает
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tar)) { $tar = 'tar.exe' }
    & $tar -xjf $file -C $dir
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $target)) { throw "не распаковалось: $name (нужен tar.exe из Windows 10 1803+)" }
    Remove-Item $file
  }
  "  готово: $($it.Result)"
}
"Лёгкий комплект на месте: $dir"
"Включить его насовсем: в klava.json  ""speechBackend"": ""lite""  (auto включит сам, если нет большого комплекта)."
