# make-icon.ps1 — генерирует icon.ico из кода, без внешних редакторов.
#
# Раскладка стандартная и максимально совместимая:
#   до 128 px  — классические DIB-кадры (их понимают и Windows, и System.Drawing.Icon)
#   256 px     — PNG (иначе кадр весит 270 КБ)
#
# Если всё складывать в PNG, System.Drawing.Icon такой файл читать откажется.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$out  = Join-Path $root 'icon.ico'

$sizes = 16, 24, 32, 48, 64, 128, 256
$blurple = [System.Drawing.Color]::FromArgb(0x58, 0x65, 0xF2)

function New-Logo([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Тот же скруглённый квадрат, что у «значка сервера» в самой программе
    $pad = [math]::Max(1, [int]($s * 0.06))
    $side = $s - $pad * 2
    $r = [math]::Max(2, [int]($side * 0.28))
    $d = $r * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $side - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $side - $d, $pad + $side - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.SolidBrush $blurple
    $g.FillPath($brush, $path)

    # На мелких размерах «MK» превращается в кашу — там оставляем одну букву
    $text = if ($s -le 32) { 'M' } else { 'MK' }
    $fontSize = if ($s -le 32) { $s * 0.52 } else { $s * 0.34 }

    $font = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center

    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.DrawString($text, $font, $white, $rect, $fmt)

    $white.Dispose(); $fmt.Dispose(); $font.Dispose()
    $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Get-DibFrame([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $bits = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $bits.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($bits.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($bits)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER: высота удвоена, потому что за цветом идёт маска прозрачности
    $bw.Write([uint32]40)
    $bw.Write([int32]$s)
    $bw.Write([int32]($s * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)          # BI_RGB
    $bw.Write([uint32]($s * $s * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # Цветные пиксели, строки снизу вверх
    for ($y = $s - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $s * 4)
    }

    # Маска 1 бит на пиксель: при 32-битном цвете с альфой она нулевая,
    # но присутствовать обязана — иначе формат считается битым
    $maskRow = [int]([math]::Floor(($s + 31) / 32) * 4)
    $zeros = New-Object byte[] $maskRow
    for ($y = 0; $y -lt $s; $y++) { $bw.Write($zeros, 0, $maskRow) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

function Get-PngFrame([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Logo $s

    if ($s -ge 256) { $frame = Get-PngFrame $bmp }
    else            { $frame = Get-DibFrame $bmp }

    $frames += ,$frame
    $bmp.Dispose()
}

# ---- контейнер ----
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }    # 0 в этом поле означает 256

    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length)
    $bw.Write([uint32]$offset)

    $offset += $frames[$i].Length
}

foreach ($f in $frames) { $bw.Write($f) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

$kb = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "  icon.ico собрана: $($sizes.Count) размеров, $kb КБ" -ForegroundColor Green
