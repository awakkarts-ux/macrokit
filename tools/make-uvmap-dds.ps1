# make-uvmap-dds.ps1 -- builds UV-probe atlases for mapping Aion UI sprites
# to exact pixel coordinates.
#
# Every pixel encodes its own position in the atlas:
#   R = x & 0xFF
#   G = y & 0xFF
#   B = 128 + (fileIndex << 4) + ((x >> 8) << 2) + (y >> 8)
#
# So a screenshot of the game tells, for any UI pixel, which file it came from
# and its exact source coordinate. Read the shot back with read-uvmap.ps1.
#
# IMPORTANT: the screenshot must be lossless (PNG). The game's own F12 shots are
# JPEG and will corrupt the encoding -- use Win+Shift+S or PrtScn instead.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: make-uvmap-dds.ps1 [-Install] | -Uninstall

param(
  [string]$Client = 'G:\Aion Empire\classic',
  [switch]$Install,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$uiDir  = Join-Path $Client 'textures\ui'
$pakOut = Join-Path $uiDir 'zz_calib.pak'

if ($Uninstall) {
  if (Test-Path $pakOut) { Remove-Item $pakOut -Force; Write-Output ("removed " + $pakOut) }
  else { Write-Output 'nothing to remove' }
  exit 0
}

# Index order matters: it is encoded in the blue channel, and the encoding
# leaves room for exactly 8 files (3 bits of index + 4 bits of coordinate high
# bits, offset by 128). The first sweep wasted half those slots on v5_* atlases
# that Classic never binds -- these are the ones actually in use, plus map
# candidates for hunting the radar rings.
$targets = @(
  @{ Name = 'v4_common.dds';   W = 1024; H = 1024 },
  @{ Name = 'v4_common_1.dds'; W = 1024; H = 1024 },
  @{ Name = 'v4_hud_s1.dds';   W = 1024; H = 512  },
  @{ Name = 'v4_hud_s2.dds';   W = 1024; H = 512  },
  @{ Name = 'v4_popup1.dds';   W = 1024; H = 1024 },
  @{ Name = 'v4_popup2.dds';   W = 1024; H = 1024 },
  @{ Name = 'v4_popup3.dds';   W = 1024; H = 512  },
  @{ Name = 'map_default.dds'; W = 512;  H = 512  }
)

function PutU32($buf, $off, $val) { [Array]::Copy([BitConverter]::GetBytes([uint32]([int64]$val -band 0xFFFFFFFFL)), 0, $buf, $off, 4) }

function Build-UvAtlas($idx, $name, $W, $H, $outPath) {
  $pixels = New-Object 'byte[]' ($W * $H * 4)
  for ($y = 0; $y -lt $H; $y++) {
    $rowBase = $y * $W * 4
    $gv = [byte]($y -band 0xFF)
    $yHi = [int]($y -shr 8)
    for ($x = 0; $x -lt $W; $x++) {
      $o = $rowBase + $x * 4
      # A8R8G8B8 little-endian on disk is B, G, R, A
      $pixels[$o]     = [byte](128 + ($idx -shl 4) + (([int]($x -shr 8)) -shl 2) + $yHi)
      $pixels[$o + 1] = $gv
      $pixels[$o + 2] = [byte]($x -band 0xFF)
      $pixels[$o + 3] = 255
    }
  }
  $hdr = New-Object 'byte[]' 128
  [Array]::Copy([System.Text.Encoding]::ASCII.GetBytes('DDS '), 0, $hdr, 0, 4)
  PutU32 $hdr 4   124
  PutU32 $hdr 8   0x0000100F
  PutU32 $hdr 12  $H
  PutU32 $hdr 16  $W
  PutU32 $hdr 20  ($W * 4)
  PutU32 $hdr 76  32
  PutU32 $hdr 80  0x41
  PutU32 $hdr 88  32
  PutU32 $hdr 92  0x00FF0000
  PutU32 $hdr 96  0x0000FF00
  PutU32 $hdr 100 0x000000FF
  PutU32 $hdr 104 0xFF000000
  PutU32 $hdr 108 0x1000
  $fsO = [System.IO.File]::Create($outPath)
  $fsO.Write($hdr, 0, 128)
  $fsO.Write($pixels, 0, $pixels.Length)
  $fsO.Close()
  Write-Output ("  [{0}] {1,-18} {2}x{3}  blue base {4}" -f $idx, $name, $W, $H, (128 + ($idx -shl 4)))
}

$tmp = Join-Path $env:TEMP 'aion-uvmap'
New-Item -ItemType Directory -Force $tmp | Out-Null
for ($i = 0; $i -lt $targets.Count; $i++) {
  Build-UvAtlas $i $targets[$i].Name $targets[$i].W $targets[$i].H (Join-Path $tmp $targets[$i].Name)
}

$pak = Join-Path $env:TEMP 'zz_calib.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
foreach ($t in $targets) {
  $e = $zip.CreateEntry($t.Name, [System.IO.Compression.CompressionLevel]::Optimal)
  $w = $e.Open()
  $bytes = [System.IO.File]::ReadAllBytes((Join-Path $tmp $t.Name))
  $w.Write($bytes, 0, $bytes.Length)
  $w.Dispose()
}
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed {0} entries -> {1} ({2:N0} bytes)" -f $targets.Count, $pak, (Get-Item $pak).Length)

if ($Install) {
  Copy-Item $pak $pakOut -Force
  Write-Output ("installed -> " + $pakOut)
  Write-Output 'restart the client; take the screenshot with Win+Shift+S (PNG), not F12'
}
