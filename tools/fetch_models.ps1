# Модели большого комплекта слуха и голоса Клавы (видеокарта NVIDIA от 8 ГБ):
#   Qwen3-TTS 0.6B Base + аудиокодек (голос, клон её эталона tts\voices\candidates\klava_02.wav)   ~3 ГБ
#   whisper large-v3-turbo q5_0 (слух на видеокарте) и small q5_1 (запасной на процессоре)      ~0,7 ГБ
#   WavLM base plus sv (кто из друзей говорит)                                                    ~0,4 ГБ
#   музыка: AST AudioSet (инструменты), wav2vec2 espeak (фонемы для пения), Demucs htdemucs_ft,
#   beat_this, звуковой банк GeneralUser GS                                                       ~2,4 ГБ
#   -Design: ещё Qwen3-TTS 1.7B VoiceDesign (4,3 ГБ) — только чтобы заново собрать голос (tts\design_voice.py)
# Уже скачанное полностью пропускается по размеру; оборвалось — запусти ещё раз, докачает.
#
#   powershell -ExecutionPolicy Bypass -File tools\fetch_models.ps1 [-Design] [-SmallVoice]
param([switch]$Design, [switch]$SmallVoice, [string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

function Get-File([string]$Url, [string]$Dest, [long]$Size) {
  New-Item -ItemType Directory -Force -Path (Split-Path $Dest -Parent) | Out-Null
  if ((Test-Path $Dest) -and $Size -gt 0 -and (Get-Item $Dest).Length -eq $Size) { "есть: $Dest"; return }
  "качаю: $Url"
  for ($try = 1; $try -le 20; $try++) {
    & curl.exe -L --fail --silent --show-error --connect-timeout 30 --speed-limit 1024 --speed-time 60 -C - -o $Dest $Url
    if ($LASTEXITCODE -eq 0) { break }
    if ($LASTEXITCODE -eq 33) { Remove-Item $Dest -ErrorAction SilentlyContinue }
    "  обрыв (curl код $LASTEXITCODE), попытка $try из 20…"
    Start-Sleep -Seconds ([math]::Min(3 * $try, 30))
  }
  if ($LASTEXITCODE -ne 0) { throw "не скачалось: $Url" }
  $got = (Get-Item $Dest).Length
  if ($Size -gt 0 -and $got -ne $Size) { throw "размер $Dest = $got, ждали $Size" }
  "  готово ({0:N1} МБ)" -f ($got / 1MB)
}

# все файлы репозитория Hugging Face с точными размерами из API
function Get-HfRepo([string]$Repo, [string]$Dest) {
  $info = Invoke-RestMethod "https://huggingface.co/api/models/$Repo`?blobs=true"
  foreach ($s in $info.siblings) {
    if ($s.rfilename -match '^\.') { continue }
    Get-File "https://huggingface.co/$Repo/resolve/main/$($s.rfilename)" (Join-Path $Dest ($s.rfilename -replace '/', '\')) ([long]$s.size)
  }
}

$m = Join-Path $Root 'models'
# голос: Qwen3-TTS (Apache-2.0)
Get-HfRepo 'Qwen/Qwen3-TTS-Tokenizer-12Hz' "$m\Qwen3-TTS-Tokenizer-12Hz"
if ($SmallVoice) {
  Get-HfRepo 'Qwen/Qwen3-TTS-12Hz-0.6B-Base' "$m\Qwen3-TTS-12Hz-0.6B-Base"
} elseif (-not (Test-Path "$m\Qwen3-TTS-12Hz-1.7B-Base-ru-stress\model.safetensors")) {
  Get-HfRepo 'Qwen/Qwen3-TTS-12Hz-1.7B-Base' "$m\Qwen3-TTS-12Hz-1.7B-Base"
  # адаптер ударений (LoRA v3, olluorg/voicy ADR 0023, Apache-2.0) — вливается в веса один раз
  foreach ($f in 'lora/adapter.json', 'lora/adapter.safetensors', 'stress.json', 'README.md') {
    Get-File "https://huggingface.co/sknyazev/qwen3-tts-12hz-1.7b-ru-stress-gguf/resolve/main/$f" "$m\stress_lora\$(Split-Path $f -Leaf)" 0
  }
  $py = Join-Path $Root 'tts\.venv\Scripts\python.exe'
  if (Test-Path $py) {
    & $py (Join-Path $Root 'tts\merge_stress_lora.py')
    if ($LASTEXITCODE -ne 0) { throw 'не получилось влить адаптер ударений' }
  } else { 'адаптер скачан; влить: tts\.venv\Scripts\python.exe tts\merge_stress_lora.py (после установки окружения)' }
}
if ($Design) { Get-HfRepo 'Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign' "$m\Qwen3-TTS-12Hz-1.7B-VoiceDesign" }
# слух: whisper.cpp (MIT)
Get-File 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin' "$m\whisper\ggml-large-v3-turbo-q5_0.bin" 0
Get-File 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin' "$m\whisper\ggml-small-q5_1.bin" 0
# кто говорит: WavLM (MIT)
Get-HfRepo 'microsoft/wavlm-base-plus-sv' "$m\speaker\wavlm-base-plus-sv"
# векторы графа памяти: bge-m3 Q8 (MIT, ~0,6 ГБ; считается на процессоре своим llama-server). Нет — память ищет по словам
Get-File 'https://huggingface.co/ggml-org/bge-m3-Q8_0-GGUF/resolve/main/bge-m3-q8_0.gguf' "$m\embed\bge-m3-q8_0.gguf" 0
# музыкальный слух и пение (music\): инструменты/жанр/настроение — AST AudioSet (BSD-3-Clause)
Get-File 'https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593/resolve/main/config.json' "$m\audio\ast-audioset\config.json" 26763
Get-File 'https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593/resolve/main/preprocessor_config.json' "$m\audio\ast-audioset\preprocessor_config.json" 297
Get-File 'https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593/resolve/main/model.safetensors' "$m\audio\ast-audioset\model.safetensors" 346404948
# фонемы для выравнивания русских слогов по нотам — wav2vec2 xlsr-53 espeak (Apache-2.0)
foreach ($f in @(@('config.json', 1856), @('preprocessor_config.json', 212), @('special_tokens_map.json', 85), @('tokenizer_config.json', 412),
                 @('vocab.json', 4637), @('pytorch_model.bin', 1263535127))) {
  Get-File "https://huggingface.co/facebook/wav2vec2-xlsr-53-espeak-cv-ft/resolve/main/$($f[0])" "$m\eval\w2v2-phoneme\$($f[0])" $f[1]
}
# дорожки — Demucs htdemucs_ft (MIT), доли — beat_this (MIT); кладутся туда, где их ищет torch.hub (TORCH_HOME = models\music\torch)
foreach ($d in 'f7e0c4bc-ba3fe64a', 'd12395a8-e57c48e6', '92cfc3b6-ef3bcb9c', '04573f0d-f3cf25b2') {
  Get-File "https://dl.fbaipublicfiles.com/demucs/hybrid_transformer/$d.th" "$m\music\torch\hub\checkpoints\$d.th" 84141271
}
Get-File 'https://cloud.cp.jku.at/public.php/dav/files/7ik4RrBKTS273gp/final0.ckpt' "$m\music\torch\hub\checkpoints\beat_this-final0.ckpt" 81058141
# звуковой банк для «похожей основы» (MIDI → звук): GeneralUser GS (своя свободная лицензия, v2.0)
Get-File 'https://github.com/mrbumpy409/GeneralUser-GS/raw/main/GeneralUser-GS.sf2' "$m\music\GeneralUser-GS.sf2" 32319396
# аватар лежит в репозитории (web\assets); если его нет — Vita (pixiv, CC0)
if (-not (Test-Path (Join-Path $Root 'web\assets\Vita.vrm'))) {
  $vita = Invoke-RestMethod 'https://api.github.com/repos/madjin/vrm-samples/contents/vroid/beta/Vita.vrm' -Headers @{ 'User-Agent' = 'klava-fetch' }
  Get-File $vita.download_url (Join-Path $Root 'web\assets\Vita.vrm') ([long]$vita.size)
}
'модели большого комплекта на месте'
