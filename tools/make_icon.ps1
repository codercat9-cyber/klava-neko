# Рисует иконку Клавы (голубая кошачья мордочка в синем круге) и сохраняет app\KlavaDesk\klava.ico.
# ICO с PNG внутри (256, 48, 32, 16 px) — так Windows 10/11 показывает её чётко на любом масштабе.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\app\KlavaDesk\klava.ico'

function New-Face([int]$n) {
  $bmp = New-Object System.Drawing.Bitmap $n, $n
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $n / 256.0
  $P = { param($x, $y) New-Object System.Drawing.PointF ([float]($x * $k)), ([float]($y * $k)) }
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $P 0 0), (& $P 0 256), ([System.Drawing.Color]::FromArgb(255, 40, 70, 170)), ([System.Drawing.Color]::FromArgb(255, 12, 20, 70))
  $g.FillEllipse($bg, [float](4 * $k), [float](4 * $k), [float](248 * $k), [float](248 * $k))
  $fur = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 140, 190, 255))
  $pink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 205, 225))
  # уши: острые треугольники, внутри розовые
  $g.FillPolygon($fur, [System.Drawing.PointF[]]@((& $P 46 140), (& $P 70 34), (& $P 120 92)))
  $g.FillPolygon($fur, [System.Drawing.PointF[]]@((& $P 210 140), (& $P 186 34), (& $P 136 92)))
  $g.FillPolygon($pink, [System.Drawing.PointF[]]@((& $P 64 124), (& $P 76 62), (& $P 106 96)))
  $g.FillPolygon($pink, [System.Drawing.PointF[]]@((& $P 192 124), (& $P 180 62), (& $P 150 96)))
  # мордочка
  $g.FillEllipse($fur, [float](44 * $k), [float](78 * $k), [float](168 * $k), [float](146 * $k))
  # глаза разного цвета: правый зелёный, левый голубой (у неё правый — слева от зрителя)
  $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 120, 210, 60))
  $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 170, 255))
  $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 25, 20, 50))
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillEllipse($green, [float](82 * $k), [float](128 * $k), [float](34 * $k), [float](40 * $k))
  $g.FillEllipse($blue, [float](140 * $k), [float](128 * $k), [float](34 * $k), [float](40 * $k))
  $g.FillEllipse($dark, [float](94 * $k), [float](134 * $k), [float](10 * $k), [float](28 * $k))
  $g.FillEllipse($dark, [float](152 * $k), [float](134 * $k), [float](10 * $k), [float](28 * $k))
  $g.FillEllipse($white, [float](88 * $k), [float](134 * $k), [float](9 * $k), [float](9 * $k))
  $g.FillEllipse($white, [float](146 * $k), [float](134 * $k), [float](9 * $k), [float](9 * $k))
  # носик и ротик «ω»
  $g.FillPolygon($pink, [System.Drawing.PointF[]]@((& $P 121 178), (& $P 135 178), (& $P 128 186)))
  if ($n -ge 32) {
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 25, 20, 50)), ([float][Math]::Max(1, 4 * $k))
    $g.DrawArc($pen, [float](110 * $k), [float](180 * $k), [float](18 * $k), [float](16 * $k), 0, 180)
    $g.DrawArc($pen, [float](128 * $k), [float](180 * $k), [float](18 * $k), [float](16 * $k), 0, 180)
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  return , $ms.ToArray()
}

$sizes = 256, 48, 32, 16
$pngs = foreach ($s in $sizes) { , (New-Face $s) }
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 пишется как 0
  $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$len); $w.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"иконка: $out ({0:N0} байт)" -f (Get-Item $out).Length
