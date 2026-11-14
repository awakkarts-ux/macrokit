# make-skin-dds.ps1 -- paints a first-pass Aion 2 style skin into a real atlas.
#
# Every sprite rectangle found by the UV mapping is filled with the style
# primitive (dark vertical gradient + hairline border, teal or gold accent for
# the few marked ones). Areas outside known rectangles stay transparent -- we
# cannot recover the original pixels, so anything the map missed simply
# disappears in game. That is acceptable for a pilot and reverses in one command.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: make-skin-dds.ps1 -Csv <sprites.csv> -Target v4_hud_s1.dds [-Install]
#        make-skin-dds.ps1 -Uninstall

param(
  [string]$Csv = 'G:\Github code\macrokit\ui-templates\sprites.csv',
  [string]$Target = 'v4_hud_s1.dds',
  [int]$Width = 0,
  [int]$Height = 0,
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Out = '',
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

$sizes = @{
  'v4_common.dds' = @(1024,1024); 'v4_popup1.dds' = @(1024,1024); 'v4_hud_s1.dds' = @(1024,512)
}
if ($Width -eq 0) { $Width = $sizes[$Target][0]; $Height = $sizes[$Target][1] }

function C([int]$a, [string]$hex) {
  return [System.Drawing.Color]::FromArgb($a,
    [Convert]::ToInt32($hex.Substring(0,2),16),
    [Convert]::ToInt32($hex.Substring(2,2),16),
    [Convert]::ToInt32($hex.Substring(4,2),16))
}
# same palette as the style board
$TOP = '1C2432'; $BOT = '080B12'; $LINE = '3A4657'; $TEAL = '4FD8E8'

$bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))   # transparent base
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None

# Known sprites, identified by eye from the overlay: the map gives coordinates,
# not meaning, so anything recognised gets its own drawing routine instead of
# the generic panel fill. Extend this table as more elements are identified.
$known = @{
  'v4_common.dds|886|9' = 'slot'      # quickslot frame, 42x43, tiled across the bar
}

function Draw-Slot($g, $x, $y, $w, $h) {
  # recessed square: near-black interior, hairline border, top catch-light
  $gr = New-Object System.Drawing.Rectangle($x, ($y - 1), $w, ($h + 2))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, (C 250 '10151F'), (C 252 '05070C'), 90.0)
  $g.FillRectangle($br, (New-Object System.Drawing.Rectangle($x, $y, $w, $h))); $br.Dispose()
  $pen = New-Object System.Drawing.Pen((C 190 '46566B'), 1.0)
  $g.DrawRectangle($pen, $x, $y, ($w - 1), ($h - 1)); $pen.Dispose()
  # inner shadow at the top, light at the bottom -- reads as a recess
  $sh = New-Object System.Drawing.Pen((C 90 '000000'), 1.0)
  $g.DrawLine($sh, ($x + 1), ($y + 1), ($x + $w - 2), ($y + 1)); $sh.Dispose()
  $li = New-Object System.Drawing.Pen((C 70 '8FA6C0'), 1.0)
  $g.DrawLine($li, ($x + 1), ($y + $h - 2), ($x + $w - 2), ($y + $h - 2)); $li.Dispose()
  # corner ticks, the Aion 2 signature detail
  $cp = New-Object System.Drawing.Pen((C 210 '6E829B'), 1.0)
  foreach ($c in @(@(0,0,1,0), @(0,0,0,1))) {
    $g.DrawLine($cp, ($x + 1), ($y + 1), ($x + 1 + $c[2] * 5), ($y + 1 + $c[3] * 5))
    $g.DrawLine($cp, ($x + $w - 2), ($y + 1), ($x + $w - 2 - $c[2] * 5), ($y + 1 + $c[3] * 5))
    $g.DrawLine($cp, ($x + 1), ($y + $h - 2), ($x + 1 + $c[2] * 5), ($y + $h - 2 - $c[3] * 5))
    $g.DrawLine($cp, ($x + $w - 2), ($y + $h - 2), ($x + $w - 2 - $c[2] * 5), ($y + $h - 2 - $c[3] * 5))
  }
  $cp.Dispose()
}

# One atlas coordinate yields several CSV rows -- across screenshots a sprite is
# caught whole in one frame and clipped in another. Keep the largest per origin,
# otherwise the same area gets painted repeatedly at conflicting sizes.
$rows = Import-Csv $Csv | Where-Object { $_.File -eq $Target }
$best = @{}
foreach ($r in $rows) {
  # rank by confirmed pixels, not by bounding-box area: the largest box for a
  # given origin is often a sparse artefact that the density filter then drops
  $k = "{0}|{1}" -f $r.AtlasX, $r.AtlasY
  if (-not $best.ContainsKey($k) -or [int]$r.Pixels -gt [int]$best[$k].Pixels) { $best[$k] = $r }
}
$rows = $best.Values
Write-Output ("unique sprite origins: {0}" -f $rows.Count)

$painted = 0
$special = 0
foreach ($r in $rows) {
  $x = [int]$r.AtlasX; $y = [int]$r.AtlasY; $w = [int]$r.W; $h = [int]$r.H
  if ($w -lt 2 -or $h -lt 2) { continue }
  $area = $w * $h
  if ($area -le 0) { continue }
  if (([double][int]$r.Pixels / $area) -lt 0.55) { continue }   # skip stretch artefacts
  if ($x + $w -gt $Width -or $y + $h -gt $Height) { continue }

  $key = "{0}|{1}|{2}" -f $Target, $x, $y
  if ($known.ContainsKey($key)) {
    switch ($known[$key]) {
      'slot' { Draw-Slot $g $x $y $w $h }
    }
    $painted++; $special++
    continue
  }

  $rect = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
  $gr = New-Object System.Drawing.Rectangle($x, ($y - 1), $w, ($h + 2))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, (C 236 $TOP), (C 248 $BOT), 90.0)
  $g.FillRectangle($br, $rect); $br.Dispose()

  # hairline border, one pixel, low contrast -- the core of the reference look
  $pen = New-Object System.Drawing.Pen((C 150 $LINE), 1.0)
  $g.DrawRectangle($pen, $x, $y, ($w - 1), ($h - 1)); $pen.Dispose()

  # top edge catch-light
  $hl = New-Object System.Drawing.Pen((C 60 '8FA0B8'), 1.0)
  $g.DrawLine($hl, ($x + 1), ($y + 1), ($x + $w - 2), ($y + 1)); $hl.Dispose()
  $painted++
}
$g.Dispose()

# ---- encode A8R8G8B8 DDS ----
$pixels = New-Object 'byte[]' ($Width * $Height * 4)
$lr = New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)
$d = $bmp.LockBits($lr, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($yy = 0; $yy -lt $Height; $yy++) {
  [System.Runtime.InteropServices.Marshal]::Copy(
    [IntPtr]::Add($d.Scan0, $yy * $d.Stride), $pixels, $yy * $Width * 4, $Width * 4)
}
$bmp.UnlockBits($d); $bmp.Dispose()

function PutU32($buf, $off, $val) { [Array]::Copy([BitConverter]::GetBytes([uint32]([int64]$val -band 0xFFFFFFFFL)), 0, $buf, $off, 4) }
$hdr = New-Object 'byte[]' 128
[Array]::Copy([System.Text.Encoding]::ASCII.GetBytes('DDS '), 0, $hdr, 0, 4)
PutU32 $hdr 4 124; PutU32 $hdr 8 0x0000100F
PutU32 $hdr 12 $Height; PutU32 $hdr 16 $Width; PutU32 $hdr 20 ($Width * 4)
PutU32 $hdr 76 32; PutU32 $hdr 80 0x41; PutU32 $hdr 88 32
PutU32 $hdr 92 0x00FF0000; PutU32 $hdr 96 0x0000FF00
PutU32 $hdr 100 0x000000FF; PutU32 $hdr 104 0xFF000000; PutU32 $hdr 108 0x1000

if ($Out -eq '') { $Out = Join-Path $env:TEMP $Target }
$fsO = [System.IO.File]::Create($Out)
$fsO.Write($hdr, 0, 128); $fsO.Write($pixels, 0, $pixels.Length); $fsO.Close()
Write-Output ("painted {0} sprites ({1} identified) into {2} ({3}x{4}, {5:N0} bytes)" -f $painted, $special, $Target, $Width, $Height, (Get-Item $Out).Length)

$pak = Join-Path $env:TEMP 'zz_skin.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
$e = $zip.CreateEntry($Target, [System.IO.Compression.CompressionLevel]::Optimal)
$wr = $e.Open()
$bytes = [System.IO.File]::ReadAllBytes($Out)
$wr.Write($bytes, 0, $bytes.Length); $wr.Dispose()
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed -> {0} ({1:N0} bytes)" -f $pak, (Get-Item $pak).Length)

if ($Install) {
  Copy-Item $pak $pakOut -Force
  Write-Output ("installed -> " + $pakOut)
  Write-Output 'remove the UV map first (make-uvmap-dds.ps1 -Uninstall), then restart the client'
}

