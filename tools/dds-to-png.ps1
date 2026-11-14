# dds-to-png.ps1 -- converts uncompressed (A8R8G8B8) DDS atlases to PNG.
#
# The UI atlases are all 32-bit, so no block decoder is needed; the compressed
# files in the pak are world maps, which are irrelevant here. Alpha is kept, and
# a second copy is written over a mid grey so transparent areas are visible.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: dds-to-png.ps1 -Src <dir> -Out <dir> [-Filter <regex>] [-OnGrey]

param(
  [Parameter(Mandatory = $true)][string]$Src,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Filter = '',
  [switch]$OnGrey
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

$done = 0; $skipped = 0
foreach ($f in (Get-ChildItem $Src -Filter '*.dds')) {
  if ($Filter -ne '' -and $f.Name -notmatch $Filter) { continue }
  $b = [System.IO.File]::ReadAllBytes($f.FullName)
  if ($b.Length -lt 128) { $skipped++; continue }
  if ([System.Text.Encoding]::ASCII.GetString($b, 0, 4) -ne 'DDS ') { $skipped++; continue }
  $bits = [BitConverter]::ToUInt32($b, 88)
  if ($bits -ne 32) { $skipped++; continue }          # block-compressed: skip
  $H = [int][BitConverter]::ToUInt32($b, 12)
  $W = [int][BitConverter]::ToUInt32($b, 16)
  if ($W -le 0 -or $H -le 0) { $skipped++; continue }
  if ($b.Length -lt (128 + $W * $H * 4)) { $skipped++; continue }

  $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $r = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  for ($y = 0; $y -lt $H; $y++) {
    [System.Runtime.InteropServices.Marshal]::Copy($b, 128 + $y * $W * 4, [IntPtr]::Add($d.Scan0, $y * $d.Stride), $W * 4)
  }
  $bmp.UnlockBits($d)

  $name = $f.Name.Replace('.dds', '')
  $bmp.Save((Join-Path $Out ($name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)

  if ($OnGrey) {
    $flat = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($flat)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 96, 100, 110))
    $g.DrawImage($bmp, 0, 0); $g.Dispose()
    $flat.Save((Join-Path $Out ($name + '_grey.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $flat.Dispose()
  }
  $bmp.Dispose()
  $done++
}
Write-Output ("converted {0}, skipped {1} (compressed or malformed)" -f $done, $skipped)
