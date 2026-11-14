# make-calib-dds.ps1 -- builds a calibration atlas for mapping Aion UI sprites.
#
# The client's own UI atlases are unreadable (encrypted), so we cannot see which
# pixel holds which sprite. This paints a numbered colour grid instead: put it in
# place of an atlas, look at the game, and every visible UI element shows which
# grid cell it came from. That yields the sprite coordinates without a key.
#
# UI atlases are uncompressed A8R8G8B8 (verified: v4_popup1.dds is
# 128 + 1024*1024*4 bytes, and the author's layout PNG is 1024x1024), so no
# block compressor is needed -- the DDS is written by hand.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage:
#   make-calib-dds.ps1 -Target v5_common.dds
#   make-calib-dds.ps1 -Target v5_hud_s1.dds -Width 1024 -Height 512 -Install
#   make-calib-dds.ps1 -Uninstall

param(
  [string]$Target = '',          # single target; empty means the built-in set below
  [int]$Width = 1024,
  [int]$Height = 1024,
  [int]$Cell = 64,
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Out = '',
  [switch]$Install,
  [switch]$Uninstall
)

# Classic ships both v4_* and v5_* atlases and we do not know which the UI
# actually binds, so calibrate several at once: each gets its own hue offset
# and a watermark naming the file, making the source obvious from one screenshot.
$targets = @(
  @{ Name = 'v5_common.dds'; W = 1024; H = 1024; Hue = 0   },
  @{ Name = 'v4_common.dds'; W = 1024; H = 1024; Hue = 72  },
  @{ Name = 'v5_hud_s1.dds'; W = 1024; H = 512;  Hue = 144 },
  @{ Name = 'v4_hud_s1.dds'; W = 1024; H = 512;  Hue = 216 },
  @{ Name = 'v4_popup1.dds'; W = 1024; H = 1024; Hue = 288 }
)
if ($Target -ne '') { $targets = @(@{ Name = $Target; W = $Width; H = $Height; Hue = 0 }) }

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$uiDir  = Join-Path $Client 'textures\ui'
$pakOut = Join-Path $uiDir 'zz_calib.pak'   # 'zz' so it sorts after ui.pak

if ($Uninstall) {
  if (Test-Path $pakOut) { Remove-Item $pakOut -Force; Write-Output ("removed " + $pakOut) }
  else { Write-Output 'nothing to remove' }
  exit 0
}

function Build-Atlas($name, $Width, $Height, $hueBase, $Cell, $Out) {
# ---- paint the grid ----------------------------------------------------------
$bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 20, 20, 20))
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::SingleBitPerPixelGridFit

$cols = [Math]::Ceiling($Width / $Cell)
$rows = [Math]::Ceiling($Height / $Cell)
$font = New-Object System.Drawing.Font('Consolas', ($Cell / 4), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255, 255), 1)

for ($r = 0; $r -lt $rows; $r++) {
  for ($c = 0; $c -lt $cols; $c++) {
    # distinct hue per cell so neighbours never share a colour;
    # hueBase separates one atlas from another at a glance
    $hue = ($hueBase + ($r * $cols + $c) * 37) % 360
    $col = [System.Drawing.Color]::FromArgb(255,
      [int](127 + 100 * [Math]::Sin(($hue + 0)   * [Math]::PI / 180)),
      [int](127 + 100 * [Math]::Sin(($hue + 120) * [Math]::PI / 180)),
      [int](127 + 100 * [Math]::Sin(($hue + 240) * [Math]::PI / 180)))
    $brush = New-Object System.Drawing.SolidBrush($col)
    $x = $c * $Cell; $y = $r * $Cell
    $g.FillRectangle($brush, $x, $y, $Cell, $Cell)
    $brush.Dispose()
    $g.DrawRectangle($linePen, $x, $y, $Cell, $Cell)
    $label = ("{0},{1}" -f $c, $r)
    $g.DrawString($label, $font, [System.Drawing.Brushes]::Black, ($x + 3), ($y + 3))
    $g.DrawString($label, $font, [System.Drawing.Brushes]::White, ($x + 2), ($y + 2))
  }
}

# watermark naming the atlas, so a screenshot shows which file a sprite came from
$wmFont = New-Object System.Drawing.Font('Consolas', ($Cell * 0.55), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$wmBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(190, 0, 0, 0))
for ($y = 0; $y -lt $Height; $y += 128) {
  $g.DrawString($name, $wmFont, $wmBrush, 6, ($y + 30))
}
$wmFont.Dispose(); $wmBrush.Dispose()
$g.Dispose()

# ---- encode as A8R8G8B8 DDS --------------------------------------------------
$pixels = New-Object 'byte[]' ($Width * $Height * 4)
$rect = New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $Height; $y++) {
  [System.Runtime.InteropServices.Marshal]::Copy(
    [IntPtr]::Add($data.Scan0, $y * $data.Stride), $pixels, $y * $Width * 4, $Width * 4)
}
$bmp.UnlockBits($data)
$bmp.Dispose()

$hdr = New-Object 'byte[]' 128
# mask through int64: PS 5.1 parses 0xFF000000 as a negative int, which [uint32] rejects
function PutU32($buf, $off, $val) { [Array]::Copy([BitConverter]::GetBytes([uint32]([int64]$val -band 0xFFFFFFFFL)), 0, $buf, $off, 4) }
[Array]::Copy([System.Text.Encoding]::ASCII.GetBytes('DDS '), 0, $hdr, 0, 4)
PutU32 $hdr 4   124                      # dwSize
PutU32 $hdr 8   0x0000100F               # CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT
PutU32 $hdr 12  $Height
PutU32 $hdr 16  $Width
PutU32 $hdr 20  ($Width * 4)             # pitch
PutU32 $hdr 24  0                        # depth
PutU32 $hdr 28  0                        # mipMapCount
PutU32 $hdr 76  32                       # pixelformat size
PutU32 $hdr 80  0x41                     # DDPF_RGB | DDPF_ALPHAPIXELS
PutU32 $hdr 84  0                        # fourCC (none)
PutU32 $hdr 88  32                       # bit count
PutU32 $hdr 92  0x00FF0000               # R mask
PutU32 $hdr 96  0x0000FF00               # G mask
PutU32 $hdr 100 0x000000FF               # B mask
PutU32 $hdr 104 0xFF000000               # A mask
PutU32 $hdr 108 0x1000                   # DDSCAPS_TEXTURE

$fsOut = [System.IO.File]::Create($Out)
$fsOut.Write($hdr, 0, 128)
$fsOut.Write($pixels, 0, $pixels.Length)
$fsOut.Close()
Write-Output ("  {0,-16} {1}x{2}  {3:N0} bytes  {4} cells" -f $name, $Width, $Height, (Get-Item $Out).Length, ($cols * $rows))
}

# ---- build every target, pack them together ----------------------------------
$tmp = Join-Path $env:TEMP 'aion-calib'
New-Item -ItemType Directory -Force $tmp | Out-Null
foreach ($t in $targets) {
  Build-Atlas $t.Name $t.W $t.H $t.Hue $Cell (Join-Path $tmp $t.Name)
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
  Write-Output 'restart the client fully; roll back with -Uninstall'
}
