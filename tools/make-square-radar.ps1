# make-square-radar.ps1 -- replaces the circular radar mask with a square one.
#
# The minimap's round shape comes from v3_radar_mask.dds (128x128), which the
# engine uses as an alpha stencil. Swapping it for a square tests whether the
# shape is driven by the mask alone or is also baked into the layout XML.
#
# The original is DXT3; we write uncompressed A8R8G8B8 instead, since the client
# has already been shown to accept that format (the UI atlases are stored that
# way). If it refuses, the radar will simply not draw -- reversible.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: make-square-radar.ps1 [-Inset 4] [-Install] | -Uninstall

param(
  [int]$Size = 128,
  [int]$Inset = 2,
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Out = '',
  [switch]$Install,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$pakOut = Join-Path (Join-Path $Client 'textures\ui') 'zz_radar.pak'
if ($Uninstall) {
  if (Test-Path $pakOut) { Remove-Item $pakOut -Force; Write-Output ("removed " + $pakOut) } else { Write-Output 'nothing to remove' }
  exit 0
}

$W = $Size; $H = $Size
$pixels = New-Object 'byte[]' ($W * $H * 4)
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    $o = ($y * $W + $x) * 4
    $inside = ($x -ge $Inset -and $x -lt ($W - $Inset) -and $y -ge $Inset -and $y -lt ($H - $Inset))
    $v = if ($inside) { 255 } else { 0 }
    $pixels[$o] = 255; $pixels[$o+1] = 255; $pixels[$o+2] = 255   # white
    $pixels[$o+3] = $v                                            # alpha is the stencil
  }
}

function PutU32($buf, $off, $val) { [Array]::Copy([BitConverter]::GetBytes([uint32]([int64]$val -band 0xFFFFFFFFL)), 0, $buf, $off, 4) }
$hdr = New-Object 'byte[]' 128
[Array]::Copy([System.Text.Encoding]::ASCII.GetBytes('DDS '), 0, $hdr, 0, 4)
PutU32 $hdr 4 124; PutU32 $hdr 8 0x0000100F
PutU32 $hdr 12 $H; PutU32 $hdr 16 $W; PutU32 $hdr 20 ($W * 4)
PutU32 $hdr 76 32; PutU32 $hdr 80 0x41; PutU32 $hdr 88 32
PutU32 $hdr 92 0x00FF0000; PutU32 $hdr 96 0x0000FF00
PutU32 $hdr 100 0x000000FF; PutU32 $hdr 104 0xFF000000; PutU32 $hdr 108 0x1000

if ($Out -eq '') { $Out = Join-Path $env:TEMP 'v3_radar_mask.dds' }
$fs = [System.IO.File]::Create($Out)
$fs.Write($hdr, 0, 128); $fs.Write($pixels, 0, $pixels.Length); $fs.Close()
Write-Output ("wrote {0} ({1}x{2}, {3:N0} bytes)" -f $Out, $W, $H, (Get-Item $Out).Length)

$pak = Join-Path $env:TEMP 'zz_radar.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
$e = $zip.CreateEntry('v3_radar_mask.dds', [System.IO.Compression.CompressionLevel]::Optimal)
$wr = $e.Open()
$bytes = [System.IO.File]::ReadAllBytes($Out)
$wr.Write($bytes, 0, $bytes.Length); $wr.Dispose()
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed -> " + $pak)
if ($Install) { Copy-Item $pak $pakOut -Force; Write-Output ("installed -> " + $pakOut) }
