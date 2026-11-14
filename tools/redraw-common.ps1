# redraw-common.ps1 -- redraws elements of v4_common.dds in the Aion 2 style.
#
# v4_common holds the pieces the engine tiles everywhere: the slot cell that
# goes under every skill and inventory icon, window frames, buttons, tabs.
# Fixing the slot here is what actually changes the skill bar -- the panel in
# v4_hud_s1 is only the backing plate behind it.
#
# Run AFTER restyle-dds.ps1 so the untouched parts share the same tonality.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: redraw-common.ps1 -Src <restyled v4_common.dds> [-Preview] [-Install]

param(
  [string]$Src = '',
  [string]$Name = 'v4_common.dds',
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Out = '',
  [switch]$Preview,
  [switch]$Install,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$pakOut = Join-Path (Join-Path $Client 'textures\ui') 'zz_common.pak'
if ($Uninstall) {
  if (Test-Path $pakOut) { Remove-Item $pakOut -Force; Write-Output ("removed " + $pakOut) } else { Write-Output 'nothing to remove' }
  exit 0
}
if ($Src -eq '') { Write-Output 'need -Src'; exit 1 }

function C([int]$a, [string]$hex) {
  return [System.Drawing.Color]::FromArgb($a,
    [Convert]::ToInt32($hex.Substring(0,2),16),
    [Convert]::ToInt32($hex.Substring(2,2),16),
    [Convert]::ToInt32($hex.Substring(4,2),16))
}
$SLOT_T = '18202E'; $SLOT_B = '070A12'
$LINE   = '52657D'; $LINE_HI = '9DB4CE'

$b = [System.IO.File]::ReadAllBytes($Src)
$H = [int][BitConverter]::ToUInt32($b, 12)
$W = [int][BitConverter]::ToUInt32($b, 16)
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$d = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $H; $y++) {
  [System.Runtime.InteropServices.Marshal]::Copy($b, 128 + $y * $W * 4, [IntPtr]::Add($d.Scan0, $y * $d.Stride), $W * 4)
}
$bmp.UnlockBits($d)

$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None

# ---- the slot cell: (886,10) .. (929,52) ----
$sx = 886; $sy = 10; $sw = 43; $sh = 42
$clear = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,0,0,0))
$g.FillRectangle($clear, ($sx - 1), ($sy - 1), ($sw + 2), ($sh + 2)); $clear.Dispose()

$gr = New-Object System.Drawing.Rectangle($sx, ($sy - 1), $sw, ($sh + 2))
$br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, (C 255 $SLOT_T), (C 255 $SLOT_B), 90.0)
$g.FillRectangle($br, (New-Object System.Drawing.Rectangle($sx, $sy, $sw, $sh))); $br.Dispose()

# hairline border
$pen = New-Object System.Drawing.Pen((C 235 $LINE), 1.0)
$g.DrawRectangle($pen, $sx, $sy, ($sw - 1), ($sh - 1)); $pen.Dispose()
# light along the bottom edge reads as a recess
$li = New-Object System.Drawing.Pen((C 90 $LINE_HI), 1.0)
$g.DrawLine($li, ($sx + 1), ($sy + $sh - 2), ($sx + $sw - 2), ($sy + $sh - 2)); $li.Dispose()
# corner ticks -- the Aion 2 signature
$cp = New-Object System.Drawing.Pen((C 255 $LINE_HI), 1.0)
$t = 5
$g.DrawLine($cp, $sx, $sy, ($sx + $t), $sy);                         $g.DrawLine($cp, $sx, $sy, $sx, ($sy + $t))
$g.DrawLine($cp, ($sx + $sw - 1), $sy, ($sx + $sw - 1 - $t), $sy);   $g.DrawLine($cp, ($sx + $sw - 1), $sy, ($sx + $sw - 1), ($sy + $t))
$g.DrawLine($cp, $sx, ($sy + $sh - 1), ($sx + $t), ($sy + $sh - 1)); $g.DrawLine($cp, $sx, ($sy + $sh - 1), $sx, ($sy + $sh - 1 - $t))
$g.DrawLine($cp, ($sx + $sw - 1), ($sy + $sh - 1), ($sx + $sw - 1 - $t), ($sy + $sh - 1))
$g.DrawLine($cp, ($sx + $sw - 1), ($sy + $sh - 1), ($sx + $sw - 1), ($sy + $sh - 1 - $t))
$cp.Dispose()

# ---- window furniture ----------------------------------------------------
# Aion 2 replaces Classic's glossy pill buttons and bevelled tabs with flat
# rectangles, hairline borders and a teal underline on the active state.

function Fill-Rect($g, $x, $y, $w, $h, $c1, $c2) {
  $gr = New-Object System.Drawing.Rectangle($x, ($y - 1), $w, ($h + 2))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, $c1, $c2, 90.0)
  $g.FillRectangle($br, (New-Object System.Drawing.Rectangle($x, $y, $w, $h))); $br.Dispose()
}
function Wipe($g, $x, $y, $w, $h) {
  $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,0,0,0))
  $g.FillRectangle($b2, $x, $y, $w, $h); $b2.Dispose()
}
function Border($g, $x, $y, $w, $h, $col, $a) {
  $pen = New-Object System.Drawing.Pen((C $a $col), 1.0)
  $g.DrawRectangle($pen, $x, $y, ($w - 1), ($h - 1)); $pen.Dispose()
}

# Depth, Aion 2 style: not a glossy bevel like Classic, but a gradient plus a
# dark line along the top-inside and a light one along the bottom-inside.
# Raised = lighter at the top; inset = darker at the top. Both keep a hairline
# outer border so the shape stays crisp at small sizes.
function Draw-Raised($g, $x, $y, $w, $h) {
  Fill-Rect $g $x $y $w $h (C 250 '2B3648') (C 252 '0D131C')
}
function Draw-Panel($g, $x, $y, $w, $h, $topHex, $botHex, $inset) {
  Fill-Rect $g $x $y $w $h (C 250 $topHex) (C 252 $botHex)
  # inner shading: one pixel in from the border
  $shadow = if ($inset) { (C 150 '000000') } else { (C 110 'B4C6DC') }
  $light  = if ($inset) { (C 90 '9DB4CE') }  else { (C 130 '000000') }
  $p1 = New-Object System.Drawing.Pen($shadow, 1.0)
  $g.DrawLine($p1, ($x + 1), ($y + 1), ($x + $w - 2), ($y + 1))
  $g.DrawLine($p1, ($x + 1), ($y + 1), ($x + 1), ($y + $h - 2)); $p1.Dispose()
  $p2 = New-Object System.Drawing.Pen($light, 1.0)
  $g.DrawLine($p2, ($x + 1), ($y + $h - 2), ($x + $w - 2), ($y + $h - 2))
  $g.DrawLine($p2, ($x + $w - 2), ($y + 1), ($x + $w - 2), ($y + $h - 2)); $p2.Dispose()
  Border $g $x $y $w $h '46566B' 235
}

# Buttons. Sliced horizontally (16px left cap, stretched middle, right cap),
# so the fill must be vertical-only -- a horizontal gradient would band when
# the middle is tiled.
# Buttons: raised, so lighter at the top. NB: do not name the loop variable $b,
# that is the DDS byte array.
foreach ($btn in @(@(0,255,52,32), @(0,395,52,32))) {
  Wipe $g $btn[0] $btn[1] $btn[2] $btn[3]
  Draw-Panel $g $btn[0] $btn[1] $btn[2] $btn[3] '32405A' '10161F' $false
}

# Tabs: active raised with a teal underline, inactive sunken and dimmer.
Wipe $g 260 25 44 62
Draw-Panel $g 262 27 40 28 '3A4A66' '141C28' $false
$tu = New-Object System.Drawing.Pen((C 240 '4FD8E8'), 2.0)
$g.DrawLine($tu, 263, 53, 300, 53); $tu.Dispose()
Draw-Panel $g 262 57 40 28 '141B26' '080C13' $true

# Checkbox: inset, so darker at the top with a light bottom edge
Wipe $g 54 203 34 30
Draw-Panel $g 56 205 26 26 '0F1620' '1B2534' $true

# Scrollbar: inset track, raised thumb
Wipe $g 110 275 42 36
Draw-Panel $g 112 277 20 32 '090D14' '141B26' $true
Draw-Panel $g 134 277 16 32 '35455E' '141C28' $false

# ---- floating panel frame: (128,535) 28x33 ----
# Nine-slice source for the frame drawn around an unpinned quickbar. The engine
# takes 10x11 corners and 7px edges out of this square, so it must stay a clean
# rounded rect with a hairline border -- no gradient, or the tiled edges band.
$fx = 128; $fy = 535; $fw = 28; $fh = 33
$clear2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,0,0,0))
$g.FillRectangle($clear2, $fx, $fy, $fw, $fh); $clear2.Dispose()
$fill = New-Object System.Drawing.SolidBrush((C 205 '0C1119'))
$g.FillRectangle($fill, $fx, $fy, $fw, $fh); $fill.Dispose()
$fpen = New-Object System.Drawing.Pen((C 230 $LINE), 1.0)
$g.DrawRectangle($fpen, $fx, $fy, ($fw - 1), ($fh - 1)); $fpen.Dispose()

$g.Dispose()

$d2 = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $H; $y++) {
  [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($d2.Scan0, $y * $d2.Stride), $b, 128 + $y * $W * 4, $W * 4)
}
$bmp.UnlockBits($d2)

if ($Out -eq '') { $Out = Join-Path $env:TEMP $Name }
[System.IO.File]::WriteAllBytes($Out, $b)
Write-Output ("wrote {0} ({1:N0} bytes)" -f $Out, (Get-Item $Out).Length)

if ($Preview) {
  $crop = $bmp.Clone((New-Object System.Drawing.Rectangle(870, 0, 80, 70)), $bmp.PixelFormat)
  $big = New-Object System.Drawing.Bitmap(480, 420)
  $gg = [System.Drawing.Graphics]::FromImage($big)
  $gg.Clear([System.Drawing.Color]::FromArgb(255, 96, 100, 110))
  $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $gg.DrawImage($crop, 0, 0, 480, 420)
  $gg.Dispose(); $crop.Dispose()
  $pv = [System.IO.Path]::ChangeExtension($Out, '.png')
  $big.Save($pv, [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
  Write-Output ("preview -> " + $pv)
}
$bmp.Dispose()

$pak = Join-Path $env:TEMP 'zz_common.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
$e = $zip.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
$wr = $e.Open()
$bytes = [System.IO.File]::ReadAllBytes($Out)
$wr.Write($bytes, 0, $bytes.Length); $wr.Dispose()
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed -> " + $pak)
if ($Install) { Copy-Item $pak $pakOut -Force; Write-Output ("installed -> " + $pakOut) }

