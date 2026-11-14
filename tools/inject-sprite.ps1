# inject-sprite.ps1 -- fits an externally produced image into an atlas slot.
#
# Workflow this supports: concepts are generated elsewhere, large and free-form;
# this script keys out their background, scales them to the exact sprite
# rectangle and writes them into the atlas. Generating at the final size never
# works -- sprites here are 26..50 px and any generator turns that into mush.
#
# Nine-slice aware: with -Slice the source is cut into corners/edges/centre and
# each piece is scaled independently, so a button's caps keep their proportions
# while the middle stretches.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage:
#   inject-sprite.ps1 -Atlas <dds> -Png <png> -X 0 -Y 255 -W 52 -H 32 [-KeyColor 000000] [-Tol 30]
#   ... -Slice 8   (nine-slice with an 8px margin in the SOURCE image)

param(
  [Parameter(Mandatory = $true)][string]$Atlas,
  [Parameter(Mandatory = $true)][string]$Png,
  [Parameter(Mandatory = $true)][int]$X,
  [Parameter(Mandatory = $true)][int]$Y,
  [Parameter(Mandatory = $true)][int]$W,
  [Parameter(Mandatory = $true)][int]$H,
  [string]$Out = '',
  [string]$KeyColor = '',
  [int]$Tol = 30,
  [int]$Slice = 0,
  [switch]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---- load the atlas ----
$b = [System.IO.File]::ReadAllBytes($Atlas)
if ([System.Text.Encoding]::ASCII.GetString($b, 0, 4) -ne 'DDS ') { Write-Output 'not a DDS'; exit 1 }
$AH = [int][BitConverter]::ToUInt32($b, 12)
$AW = [int][BitConverter]::ToUInt32($b, 16)
$atlas = New-Object System.Drawing.Bitmap($AW, $AH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$ar = New-Object System.Drawing.Rectangle(0, 0, $AW, $AH)
$ad = $atlas.LockBits($ar, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($yy = 0; $yy -lt $AH; $yy++) {
  [System.Runtime.InteropServices.Marshal]::Copy($b, 128 + $yy * $AW * 4, [IntPtr]::Add($ad.Scan0, $yy * $ad.Stride), $AW * 4)
}
$atlas.UnlockBits($ad)

# ---- load the source and key out its background ----
$srcRaw = New-Object System.Drawing.Bitmap($Png)
$src = New-Object System.Drawing.Bitmap($srcRaw.Width, $srcRaw.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gs = [System.Drawing.Graphics]::FromImage($src)
$gs.DrawImage($srcRaw, 0, 0, $srcRaw.Width, $srcRaw.Height)
$gs.Dispose(); $srcRaw.Dispose()

if ($KeyColor -ne '') {
  # Generators rarely emit real alpha, so a flat backdrop is keyed out here.
  $kr = [Convert]::ToInt32($KeyColor.Substring(0,2),16)
  $kg = [Convert]::ToInt32($KeyColor.Substring(2,2),16)
  $kb = [Convert]::ToInt32($KeyColor.Substring(4,2),16)
  $sr = New-Object System.Drawing.Rectangle(0, 0, $src.Width, $src.Height)
  $sd = $src.LockBits($sr, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $buf = New-Object 'byte[]' ($sd.Stride * $src.Height)
  [System.Runtime.InteropServices.Marshal]::Copy($sd.Scan0, $buf, 0, $buf.Length)
  for ($i = 0; $i -lt $buf.Length; $i += 4) {
    $dr = [Math]::Abs([int]$buf[$i+2] - $kr)
    $dg = [Math]::Abs([int]$buf[$i+1] - $kg)
    $db = [Math]::Abs([int]$buf[$i]   - $kb)
    if (($dr + $dg + $db) -le $Tol) { $buf[$i+3] = 0 }
  }
  [System.Runtime.InteropServices.Marshal]::Copy($buf, 0, $sd.Scan0, $buf.Length)
  $src.UnlockBits($sd)
}

$g = [System.Drawing.Graphics]::FromImage($atlas)
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# clear the target first, or leftovers show through transparent parts
$clr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,0,0,0))
$g.FillRectangle($clr, $X, $Y, $W, $H); $clr.Dispose()

if ($Slice -gt 0) {
  # nine-slice: corners keep their size, edges stretch along one axis
  $m = $Slice
  $dm = [Math]::Min($m, [Math]::Min([int]($W/2), [int]($H/2)))
  $sw = $src.Width; $sh = $src.Height
  $cols = @(@(0,$m,0,$dm), @($m,($sw-2*$m),$dm,($W-2*$dm)), @(($sw-$m),$m,($W-$dm),$dm))
  $rows = @(@(0,$m,0,$dm), @($m,($sh-2*$m),$dm,($H-2*$dm)), @(($sh-$m),$m,($H-$dm),$dm))
  foreach ($c in $cols) {
    foreach ($r in $rows) {
      if ($c[1] -le 0 -or $r[1] -le 0 -or $c[3] -le 0 -or $r[3] -le 0) { continue }
      $dst = New-Object System.Drawing.Rectangle(($X + $c[2]), ($Y + $r[2]), $c[3], $r[3])
      $g.DrawImage($src, $dst, $c[0], $r[0], $c[1], $r[1], [System.Drawing.GraphicsUnit]::Pixel)
    }
  }
} else {
  $dst = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
  $g.DrawImage($src, $dst, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel)
}
$g.Dispose(); $src.Dispose()

# ---- write the atlas back ----
$ad2 = $atlas.LockBits($ar, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($yy = 0; $yy -lt $AH; $yy++) {
  [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($ad2.Scan0, $yy * $ad2.Stride), $b, 128 + $yy * $AW * 4, $AW * 4)
}
$atlas.UnlockBits($ad2)

if ($Out -eq '') { $Out = $Atlas }
[System.IO.File]::WriteAllBytes($Out, $b)
Write-Output ("injected {0} -> {1} at {2},{3} {4}x{5}{6}" -f (Split-Path $Png -Leaf), (Split-Path $Out -Leaf), $X, $Y, $W, $H, $(if ($Slice) { " (nine-slice $Slice)" } else { '' }))

if ($Preview) {
  $crop = $atlas.Clone((New-Object System.Drawing.Rectangle(([Math]::Max(0,$X-8)), ([Math]::Max(0,$Y-8)), ($W+16), ($H+16))), $atlas.PixelFormat)
  $z = 6
  $big = New-Object System.Drawing.Bitmap(($crop.Width*$z), ($crop.Height*$z))
  $gg = [System.Drawing.Graphics]::FromImage($big)
  $gg.Clear([System.Drawing.Color]::FromArgb(255,96,100,110))
  $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $gg.DrawImage($crop, 0, 0, $big.Width, $big.Height)
  $gg.Dispose(); $crop.Dispose()
  $pv = [System.IO.Path]::ChangeExtension($Out, '.inject.png')
  $big.Save($pv, [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
  Write-Output ("preview -> " + $pv)
}
$atlas.Dispose()
