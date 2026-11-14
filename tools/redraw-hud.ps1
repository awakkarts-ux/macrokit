# redraw-hud.ps1 -- replaces individual HUD sprites with Aion 2 style versions.
#
# Unlike restyle-dds.ps1 (which only recolours), this erases a sprite's
# rectangle and draws a new element from scratch. Geometry stays inside the
# original bounds because the UVs are baked into the client's binary XML --
# same footprint, different look.
#
# Coordinates were read off the decrypted atlas (see ui-original\).
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: redraw-hud.ps1 -Src <original.dds> -Out <new.dds> [-Preview] [-Install]

param(
  [string]$Src = 'G:\Github code\macrokit\ui-original\v4_hud_s1.dds',
  [string]$Out = '',
  [string]$Name = 'v4_hud_s1.dds',
  [string]$Client = 'G:\Aion Empire\classic',
  [switch]$Preview,
  [switch]$Install,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$uiDir  = Join-Path $Client 'textures\ui'
$pakOut = Join-Path $uiDir 'zz_skin.pak'
if ($Uninstall) {
  if (Test-Path $pakOut) { Remove-Item $pakOut -Force; Write-Output ("removed " + $pakOut) } else { Write-Output 'nothing to remove' }
  exit 0
}

function C([int]$a, [string]$hex) {
  return [System.Drawing.Color]::FromArgb($a,
    [Convert]::ToInt32($hex.Substring(0,2),16),
    [Convert]::ToInt32($hex.Substring(2,2),16),
    [Convert]::ToInt32($hex.Substring(4,2),16))
}
# Aion 2 palette
$SLOT_T = '141A26'; $SLOT_B = '05070C'
$LINE   = '46566B'; $LINE_HI = '8FA6C0'
$TEAL   = '4FD8E8'; $BAR_BG = '0A0E16'

# ---- load DDS into a bitmap ----
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

function Clear-Area($g, $x, $y, $w, $h) {
  $br = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,0,0,0))
  $g.FillRectangle($br, $x, $y, $w, $h); $br.Dispose()
}

# one slot: recessed dark square, hairline border, corner ticks
function Draw-Slot($g, $x, $y, $w, $h) {
  $gr = New-Object System.Drawing.Rectangle($x, ($y - 1), $w, ($h + 2))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, (C 245 $SLOT_T), (C 250 $SLOT_B), 90.0)
  $g.FillRectangle($br, (New-Object System.Drawing.Rectangle($x, $y, $w, $h))); $br.Dispose()
  $pen = New-Object System.Drawing.Pen((C 200 $LINE), 1.0)
  $g.DrawRectangle($pen, $x, $y, ($w - 1), ($h - 1)); $pen.Dispose()
  $li = New-Object System.Drawing.Pen((C 70 $LINE_HI), 1.0)
  $g.DrawLine($li, ($x + 1), ($y + $h - 2), ($x + $w - 2), ($y + $h - 2)); $li.Dispose()
  $cp = New-Object System.Drawing.Pen((C 220 $LINE_HI), 1.0)
  $t = 4
  $g.DrawLine($cp, ($x+1), ($y+1), ($x+1+$t), ($y+1))
  $g.DrawLine($cp, ($x+1), ($y+1), ($x+1), ($y+1+$t))
  $g.DrawLine($cp, ($x+$w-2), ($y+1), ($x+$w-2-$t), ($y+1))
  $g.DrawLine($cp, ($x+$w-2), ($y+1), ($x+$w-2), ($y+1+$t))
  $g.DrawLine($cp, ($x+1), ($y+$h-2), ($x+1+$t), ($y+$h-2))
  $g.DrawLine($cp, ($x+1), ($y+$h-2), ($x+1), ($y+$h-2-$t))
  $g.DrawLine($cp, ($x+$w-2), ($y+$h-2), ($x+$w-2-$t), ($y+$h-2))
  $g.DrawLine($cp, ($x+$w-2), ($y+$h-2), ($x+$w-2), ($y+$h-2-$t))
  $cp.Dispose()
}

# ---- quickbar backing plate ----
# The skill bar's plate is the thin strip at y=492, sliced into three columns
# (384/454/532), each 18px tall -- NOT the wide bands further up, which belong
# to the portrait and status area. Getting this wrong put a stray dark band on
# screen under the lock button.
Clear-Area $g 384 492 219 18
$plate = New-Object System.Drawing.SolidBrush((C 235 $BAR_BG))
$g.FillRectangle($plate, 384, 494, 219, 14); $plate.Dispose()
$pp = New-Object System.Drawing.Pen((C 170 $LINE), 1.0)
$g.DrawLine($pp, 384, 494, 602, 494)
$g.DrawLine($pp, 384, 507, 602, 507); $pp.Dispose()

# (the wide band at y 428..494 is left alone: it belongs to another dialog,
#  not to the skill bar)

# Portrait ring. Antialiasing must be on here -- a circle drawn with SourceCopy
# and no smoothing comes out visibly stepped.
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

function Draw-Ring($g, $cx, $cy, $r) {
  # dark disc with a thin cool rim and a faint inner sheen
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddEllipse(($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
  $pb = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
  $pb.CenterColor = (C 250 '0A0E16')
  $pb.SurroundColors = @((C 250 '1A2230'))
  $g.FillPath($pb, $path); $pb.Dispose()
  $pen = New-Object System.Drawing.Pen((C 210 $LINE), 2.0)
  $g.DrawEllipse($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2)); $pen.Dispose()
  $pen = New-Object System.Drawing.Pen((C 90 $LINE_HI), 1.0)
  $g.DrawEllipse($pen, ($cx - $r + 3), ($cy - $r + 3), (($r - 3) * 2), (($r - 3) * 2)); $pen.Dispose()
  # teal arc at the top: the Aion 2 accent, used sparingly
  $pen = New-Object System.Drawing.Pen((C 200 $TEAL), 2.0)
  $g.DrawArc($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2), 200, 140); $pen.Dispose()
  $path.Dispose()
}

$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
Clear-Area $g 0 360 146 142
Clear-Area $g 458 96 186 198
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
Draw-Ring $g 71 432 66
Draw-Ring $g 551 195 90

# ---- status bars: deliberately NOT touched ----
# The atlas holds only the dark troughs here; the coloured HP/MP fill is
# produced by the engine from these very pixels. Clearing the area killed the
# colour, so the region is left to the recolour pass alone.

$g.Dispose()

# ---- write back ----
$d2 = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $H; $y++) {
  [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($d2.Scan0, $y * $d2.Stride), $b, 128 + $y * $W * 4, $W * 4)
}
$bmp.UnlockBits($d2)

if ($Out -eq '') { $Out = Join-Path $env:TEMP $Name }
[System.IO.File]::WriteAllBytes($Out, $b)
Write-Output ("wrote {0} ({1:N0} bytes)" -f $Out, (Get-Item $Out).Length)

if ($Preview) {
  $flat = New-Object System.Drawing.Bitmap($W, $H)
  $gg = [System.Drawing.Graphics]::FromImage($flat)
  $gg.Clear([System.Drawing.Color]::FromArgb(255, 90, 95, 105))
  $gg.DrawImage($bmp, 0, 0); $gg.Dispose()
  $pv = [System.IO.Path]::ChangeExtension($Out, '.png')
  $flat.Save($pv, [System.Drawing.Imaging.ImageFormat]::Png); $flat.Dispose()
  Write-Output ("preview -> " + $pv)
}
$bmp.Dispose()

$pak = Join-Path $env:TEMP 'zz_skin.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
$e = $zip.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
$wr = $e.Open()
$bytes = [System.IO.File]::ReadAllBytes($Out)
$wr.Write($bytes, 0, $bytes.Length); $wr.Dispose()
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed -> {0}" -f $pak)
if ($Install) { Copy-Item $pak $pakOut -Force; Write-Output ("installed -> " + $pakOut) }
