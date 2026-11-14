# sprite-sheet.ps1 -- cuts named sprite rectangles out of the decrypted atlases
# and lays them out on one labelled sheet, magnified.
#
# Purpose: review a UI zone as a set of actual pieces before redrawing it.
# Reading coordinates off a CSV is error-prone -- twice already a plate was
# redrawn in the wrong place because a region's screen position was misread.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: sprite-sheet.ps1 -Spec <path to spec file> -Out sheet.png [-Zoom 4]
#   spec lines: <atlas file>|<x>|<y>|<w>|<h>|<label>

param(
  [Parameter(Mandatory = $true)][string]$Spec,
  [Parameter(Mandatory = $true)][string]$Out,
  [int]$Zoom = 4,
  [string]$Orig = 'G:\Github code\macrokit\ui-original'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Load-Dds($path) {
  $b = [System.IO.File]::ReadAllBytes($path)
  $H = [int][BitConverter]::ToUInt32($b, 12)
  $W = [int][BitConverter]::ToUInt32($b, 16)
  $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $r = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  for ($y = 0; $y -lt $H; $y++) {
    [System.Runtime.InteropServices.Marshal]::Copy($b, 128 + $y * $W * 4, [IntPtr]::Add($d.Scan0, $y * $d.Stride), $W * 4)
  }
  $bmp.UnlockBits($d)
  return $bmp
}

$cache = @{}
$items = @()
foreach ($line in (Get-Content $Spec -Encoding UTF8)) {
  if ($line.Trim() -eq '' -or $line.StartsWith('#')) { continue }
  $p = $line.Split('|')
  $items += [pscustomobject]@{
    File = $p[0]; X = [int]$p[1]; Y = [int]$p[2]; W = [int]$p[3]; H = [int]$p[4]; Label = $p[5]
  }
}

# layout: one row per sprite, label on the left
$rowH = 0
foreach ($i in $items) { $rowH += [Math]::Max(($i.H * $Zoom), 26) + 16 }
$maxW = 0
foreach ($i in $items) { if (($i.W * $Zoom) -gt $maxW) { $maxW = $i.W * $Zoom } }
$labelW = 250
$sheet = New-Object System.Drawing.Bitmap(($labelW + $maxW + 40), ($rowH + 20))
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(255, 88, 92, 102))
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$f = New-Object System.Drawing.Font('Consolas', 11)
$fb = New-Object System.Drawing.Font('Consolas', 11, [System.Drawing.FontStyle]::Bold)

$y = 10
foreach ($i in $items) {
  if (-not $cache.ContainsKey($i.File)) { $cache[$i.File] = Load-Dds (Join-Path $Orig $i.File) }
  $src = $cache[$i.File]
  $g.DrawString($i.Label, $fb, [System.Drawing.Brushes]::White, 8, $y)
  $g.DrawString(("{0} {1},{2} {3}x{4}" -f $i.File.Replace('.dds',''), $i.X, $i.Y, $i.W, $i.H), $f,
    (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,180,190,205))), 8, ($y + 18))
  $dst = New-Object System.Drawing.Rectangle($labelW, $y, ($i.W * $Zoom), ($i.H * $Zoom))
  $g.DrawImage($src, $dst, $i.X, $i.Y, $i.W, $i.H, [System.Drawing.GraphicsUnit]::Pixel)
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 0, 255, 255), 1)
  $g.DrawRectangle($pen, $dst); $pen.Dispose()
  $y += [Math]::Max(($i.H * $Zoom), 26) + 16
}
$g.Dispose()
foreach ($k in $cache.Keys) { $cache[$k].Dispose() }
$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Write-Output ("wrote " + $Out)
