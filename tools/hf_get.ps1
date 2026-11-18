# Быстрое скачивание большого файла с Hugging Face в несколько потоков (как aria2, но на curl.exe из Windows).
# Файл режется на куски по диапазонам байт, каждый кусок качает свой curl. Обрыв ничего не теряет: curl пишет во
# временный файл, после каждой попытки полученные байты дописываются в кусок, и следующая попытка просит только
# остаток. (Встроенный --retry у curl при повторе начинает диапазон заново и затирает файл — так 15 ГБ ушли в ноль.)
# В конце куски склеиваются и проверяется SHA-256 из API Hugging Face. Прервал сам — запусти ещё раз, докачает.
#
#   powershell -ExecutionPolicy Bypass -File hf_get.ps1 -Repo HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive `
#       -File Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-Q6_K_P.gguf -OutDir H:\Neiro\Neko\models\brain
param(
  [Parameter(Mandatory)] [string]$Repo,
  [Parameter(Mandatory)] [string]$File,
  [Parameter(Mandatory)] [string]$OutDir,
  [int]$Parts = 12,
  [string]$Revision = 'main'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dest = Join-Path $OutDir (Split-Path $File -Leaf)
$url = "https://huggingface.co/$Repo/resolve/$Revision/$File"

# размер и sha256 — из API (у LFS-файлов это oid)
$tree = Invoke-RestMethod "https://huggingface.co/api/models/$Repo/tree/$Revision`?recursive=true"
$meta = $tree | Where-Object { $_.path -eq $File } | Select-Object -First 1
if (-not $meta) { throw "в $Repo нет файла $File" }
$size = [long]($(if ($meta.lfs) { $meta.lfs.size } else { $meta.size }))
$sha = if ($meta.lfs) { $meta.lfs.oid } else { $null }
"{0}: {1:N2} ГБ, sha256 {2}" -f $File, ($size / 1GB), $sha

if ((Test-Path $dest) -and (Get-Item $dest).Length -eq $size) {
  if (-not $sha -or (Get-FileHash $dest -Algorithm SHA256).Hash -ieq $sha) { "уже скачан и проверен: $dest"; exit 0 }
  "размер совпал, а хеш нет — перекачиваю"; Remove-Item $dest
}

# маленькие файлы — одним куском
if ($size -lt 256MB) { $Parts = 1 }
$chunk = [long][math]::Ceiling($size / $Parts)

function Get-Have($s) { if (Test-Path $s.Part) { (Get-Item $s.Part).Length } else { [long]0 } }

# дописать полученное последней попыткой в кусок (не больше, чем куску положено)
function Merge-Tmp($s) {
  if (-not (Test-Path $s.Tmp)) { return }
  $room = $s.Need - (Get-Have $s)
  $out = [IO.File]::Open($s.Part, [IO.FileMode]::Append, [IO.FileAccess]::Write)
  try {
    $in = [IO.File]::OpenRead($s.Tmp)
    try {
      $buf = New-Object byte[] (4MB)
      # всё в long: иначе PowerShell берёт Min(int,int) и падает на кусках больше 2 ГБ
      [long]$left = [math]::Min([long]$room, [long]$in.Length)
      while ($left -gt 0) {
        $n = $in.Read($buf, 0, [int][math]::Min([long]$buf.Length, $left))
        if ($n -le 0) { break }
        $out.Write($buf, 0, $n)
        $left -= $n
      }
    } finally { $in.Dispose() }
  } finally { $out.Dispose() }
  Remove-Item $s.Tmp
}

function Start-Part($s) {
  $have = Get-Have $s
  # --speed-limit/--speed-time: зависшее соединение (меньше 1 КБ/с минуту) рвём сами — цикл докачает
  $cargs = @('-L', '--fail', '--silent', '--show-error', '--connect-timeout', '30', '--speed-limit', '1024', '--speed-time', '60',
            '-r', "$($s.From + $have)-$($s.To)", '-o', $s.Tmp, $url)
  $s.Proc = Start-Process curl.exe -ArgumentList $cargs -NoNewWindow -PassThru
  $null = $s.Proc.Handle   # без этого PowerShell теряет код выхода процесса
}

$states = @()
for ($i = 0; $i -lt $Parts; $i++) {
  $from = [long]$i * $chunk
  $to = [long][math]::Min([long]$size - 1, [long]$from + $chunk - 1)
  $s = [pscustomobject]@{ I = $i; From = $from; To = $to; Need = $to - $from + 1; Part = "$dest.part$i"; Tmp = "$dest.part$i.tmp"
                          Proc = $null; Fails = 0; NextAt = [datetime]::MinValue }
  Merge-Tmp $s   # хвост прошлого запуска
  if ((Get-Have $s) -gt $s.Need) { Remove-Item $s.Part }   # кусок от другой нарезки — заново
  $states += $s
}

$t0 = Get-Date
$got0 = ($states | ForEach-Object { Get-Have $_ } | Measure-Object -Sum).Sum
$tick = 0
while ($true) {
  $active = 0
  foreach ($s in $states) {
    if ($s.Proc -and $s.Proc.HasExited) {
      $code = $s.Proc.ExitCode
      $s.Proc = $null
      $before = Get-Have $s
      Merge-Tmp $s
      $have = Get-Have $s
      if ($have -lt $s.Need) {
        if ($have -gt $before) { $s.Fails = 0 } else { $s.Fails++ }
        if ($s.Fails -ge 40) { throw "кусок $($s.I): 40 попыток подряд без единого байта (curl код $code) — проверь сеть и запусти ещё раз, докачает" }
        $s.NextAt = (Get-Date).AddSeconds([math]::Min(3 * $s.Fails, 30))
        "кусок {0}: обрыв (curl код {1}), продолжу с {2:N0} МБ" -f $s.I, $code, ($have / 1MB)
      }
    }
    if (-not $s.Proc -and (Get-Have $s) -lt $s.Need -and (Get-Date) -ge $s.NextAt) { Start-Part $s }
    if ($s.Proc -or (Get-Have $s) -lt $s.Need) { $active++ }
  }
  if ($active -eq 0) { break }
  Start-Sleep 2
  if ((++$tick % 5) -eq 0) {
    $got = [long]0
    foreach ($s in $states) { $got += Get-Have $s; if (Test-Path $s.Tmp) { $got += (Get-Item $s.Tmp).Length } }
    $sec = [math]::Max(1, ((Get-Date) - $t0).TotalSeconds)
    "{0:N1} / {1:N1} ГБ  ({2:N1} МБ/с)" -f ($got / 1GB), ($size / 1GB), (($got - $got0) / 1MB / $sec)
  }
}

# склейка кусков
"склеиваю…"
$out = [IO.File]::Create($dest)
try {
  foreach ($s in $states) {
    $in = [IO.File]::OpenRead($s.Part)
    try { $in.CopyTo($out, 16MB) } finally { $in.Dispose() }
  }
} finally { $out.Dispose() }
if ((Get-Item $dest).Length -ne $size) { throw "после склейки размер не тот" }
if ($sha) {
  "проверяю sha256…"
  $h = (Get-FileHash $dest -Algorithm SHA256).Hash
  if ($h -ine $sha) { Remove-Item $dest; throw "sha256 не совпал ($h), файл удалён — куски оставлены, запусти ещё раз" }
}
foreach ($s in $states) { Remove-Item $s.Part -ErrorAction SilentlyContinue }
"ГОТОВО: $dest"
