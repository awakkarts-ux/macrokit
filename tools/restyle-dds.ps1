# restyle-dds.ps1 -- recolours a decrypted UI atlas into the Aion 2 palette and
# packs it back for the client.
#
# Working from the real atlas (see unpak.ps1) instead of drawing blind: every
# sprite keeps its exact geometry, alpha and detail, and only the tonality
# changes. Luminance is remapped through a dark blue-steel ramp, shadows are
# deepened and highlights kept cool, which is what separates the Aion 2 look
# from Classic's warm, glossy chrome.
#
# The client reads a plain unencrypted ZIP renamed to .pak, so no re-encryption
# is needed on the way back.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage:
#   restyle-dds.ps1 -Src <original.dds> -Name v4_hud_s1.dds [-Install]
#   restyle-dds.ps1 -Uninstall

param(
  [string]$Src = '',
  [string]$Name = 'v4_hud_s1.dds',
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Out = '',
  [double]$Contrast = 1.15,
  # 18 verified on v4_common_1: HP/MP fills and icons keep their colour,
  # neutral chrome still restyles. Higher values grey out gradient tails.
  [int]$KeepSaturation = 18,
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
  if (Test-Path $pakOut) { Remove-Item $pakOut -Force; Write-Output ("removed " + $pakOut) }
  else { Write-Output 'nothing to remove' }
  exit 0
}
if ($Src -eq '') { Write-Output 'need -Src'; exit 1 }

$b = [System.IO.File]::ReadAllBytes($Src)
if ([System.Text.Encoding]::ASCII.GetString($b, 0, 4) -ne 'DDS ') { Write-Output 'not a DDS'; exit 1 }
# cast to int: the Bitmap constructor rejects uint32
$H = [int][BitConverter]::ToUInt32($b, 12)
$W = [int][BitConverter]::ToUInt32($b, 16)
$bits = [BitConverter]::ToUInt32($b, 88)
if ($bits -ne 32) { Write-Output ("unsupported bit depth: " + $bits); exit 1 }
Write-Output ("source {0}  {1}x{2}  32bpp" -f (Split-Path $Src -Leaf), $W, $H)

# Palette ramp: shadow -> midtone -> highlight. Cool and desaturated, matching
# the reference, with a slight blue lift in the darks so black areas read as
# deep navy rather than dead flat.
$ramp = New-Object 'byte[]' 768      # 256 entries x RGB
$stops = @(
  @(0.00, 4,   6,  11),
  @(0.25, 16,  21,  31),
  @(0.50, 40,  50,  66),
  @(0.75, 92, 106, 126),
  @(1.00, 196, 210, 228)
)
for ($i = 0; $i -lt 256; $i++) {
  $t = $i / 255.0
  # contrast around the midpoint, applied before the ramp lookup
  $t = [Math]::Max(0.0, [Math]::Min(1.0, ($t - 0.5) * $Contrast + 0.5))
  $s = 0
  while ($s -lt ($stops.Count - 2) -and $t -gt $stops[$s + 1][0]) { $s++ }
  $a = $stops[$s]; $c = $stops[$s + 1]
  $span = $c[0] - $a[0]
  $f = if ($span -le 0) { 0.0 } else { ($t - $a[0]) / $span }
  $ramp[$i * 3]     = [byte][Math]::Round($a[1] + ($c[1] - $a[1]) * $f)
  $ramp[$i * 3 + 1] = [byte][Math]::Round($a[2] + ($c[2] - $a[2]) * $f)
  $ramp[$i * 3 + 2] = [byte][Math]::Round($a[3] + ($c[3] - $a[3]) * $f)
}

# ---- remap pixels (data is B,G,R,A on disk) ----
$off = 128
$count = $W * $H
for ($i = 0; $i -lt $count; $i++) {
  $o = $off + $i * 4
  $al = $b[$o + 3]
  if ($al -eq 0) { continue }              # fully transparent: leave alone
  $bl = $b[$o]; $gr = $b[$o + 1]; $rd = $b[$o + 2]

  # Leave saturated pixels alone. The atlases carry meaningful colour -- the red
  # and blue HP/MP fills, item-rarity tints, icon art -- and pushing those
  # through a luminance ramp turns them grey. Only neutral chrome gets restyled.
  $mx = [Math]::Max($rd, [Math]::Max($gr, $bl))
  $mn = [Math]::Min($rd, [Math]::Min($gr, $bl))
  if (($mx - $mn) -ge $KeepSaturation) { continue }

  # Rec.601 luma -- perceptually closer than a plain average
  $lum = [int](0.299 * $rd + 0.587 * $gr + 0.114 * $bl)
  if ($lum -gt 255) { $lum = 255 }
  $b[$o]     = $ramp[$lum * 3 + 2]
  $b[$o + 1] = $ramp[$lum * 3 + 1]
  $b[$o + 2] = $ramp[$lum * 3]
}

if ($Out -eq '') { $Out = Join-Path $env:TEMP $Name }
[System.IO.File]::WriteAllBytes($Out, $b)
Write-Output ("wrote {0} ({1:N0} bytes)" -f $Out, (Get-Item $Out).Length)

if ($Preview) {
  $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $r = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  for ($y = 0; $y -lt $H; $y++) {
    [System.Runtime.InteropServices.Marshal]::Copy($b, 128 + $y * $W * 4, [IntPtr]::Add($d.Scan0, $y * $d.Stride), $W * 4)
  }
  $bmp.UnlockBits($d)
  $flat = New-Object System.Drawing.Bitmap($W, $H)
  $g = [System.Drawing.Graphics]::FromImage($flat)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 90, 95, 105))
  $g.DrawImage($bmp, 0, 0); $g.Dispose(); $bmp.Dispose()
  $pv = [System.IO.Path]::ChangeExtension($Out, '.png')
  $flat.Save($pv, [System.Drawing.Imaging.ImageFormat]::Png); $flat.Dispose()
  Write-Output ("preview -> " + $pv)
}

$pak = Join-Path $env:TEMP 'zz_skin.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
$e = $zip.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
$wr = $e.Open()
$bytes = [System.IO.File]::ReadAllBytes($Out)
$wr.Write($bytes, 0, $bytes.Length); $wr.Dispose()
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed -> {0} ({1:N0} bytes)" -f $pak, (Get-Item $pak).Length)

if ($Install) {
  Copy-Item $pak $pakOut -Force
  Write-Output ("installed -> " + $pakOut)
}
