# build-all.ps1 -- builds the whole Aion 2 reskin into ONE pak and installs it.
#
# Separate paks per file are risky: if the client only picks up a single extra
# pak from the folder, part of the changes silently never arrive. One pak with
# every modified texture removes that whole class of problem.
#
# Pipeline per atlas: unpak.ps1 has already produced the originals in
# ui-original\; restyle-dds.ps1 recolours, then redraw-*.ps1 replaces elements.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: build-all.ps1 [-Contrast 1.45] [-SquareRadar] [-Install] | -Uninstall

param(
  [double]$Contrast = 1.45,
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Orig = 'G:\Github code\macrokit\ui-original',
  [switch]$SquareRadar,
  [switch]$Install,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$uiDir  = Join-Path $Client 'textures\ui'
$pakOut = Join-Path $uiDir 'zz_aion2.pak'
$tools  = 'G:\Github code\macrokit\tools'

if ($Uninstall) {
  # clear every experimental pak this project may have left behind
  foreach ($n in @('zz_aion2.pak','zz_skin.pak','zz_common.pak','zz_radar.pak','zz_calib.pak')) {
    $p = Join-Path $uiDir $n
    if (Test-Path $p) { Remove-Item $p -Force; Write-Output ("removed " + $n) }
  }
  exit 0
}

$work = Join-Path $env:TEMP 'aion2-build'
New-Item -ItemType Directory -Force $work | Out-Null
$staged = @{}

# ---- HUD atlas ----
$hudBase = Join-Path $work 'hud_base.dds'
$hudOut  = Join-Path $work 'v4_hud_s1.dds'
& (Join-Path $tools 'restyle-dds.ps1') -Src (Join-Path $Orig 'v4_hud_s1.dds') -Name 'v4_hud_s1.dds' -Out $hudBase -Contrast $Contrast | Out-Null
& (Join-Path $tools 'redraw-hud.ps1')  -Src $hudBase -Out $hudOut | Out-Null
$staged['v4_hud_s1.dds'] = $hudOut
Write-Output 'v4_hud_s1.dds  restyled + redrawn'

# ---- common atlas (this is where the real slot cell lives) ----
$comBase = Join-Path $work 'common_base.dds'
$comOut  = Join-Path $work 'v4_common.dds'
& (Join-Path $tools 'restyle-dds.ps1')  -Src (Join-Path $Orig 'v4_common.dds') -Name 'v4_common.dds' -Out $comBase -Contrast $Contrast | Out-Null
& (Join-Path $tools 'redraw-common.ps1') -Src $comBase -Out $comOut | Out-Null
$staged['v4_common.dds'] = $comOut
Write-Output 'v4_common.dds  restyled + redrawn'

# ---- companion atlases: recolour only, so tone stays consistent ----
foreach ($n in @('v4_popup1.dds','v4_popup2.dds','v4_popup3.dds','v4_common_1.dds','v4_hud_s2.dds')) {
  $src = Join-Path $Orig $n
  if (-not (Test-Path $src)) { continue }
  $dst = Join-Path $work $n
  & (Join-Path $tools 'restyle-dds.ps1') -Src $src -Name $n -Out $dst -Contrast $Contrast | Out-Null
  $staged[$n] = $dst
  Write-Output ("{0}  restyled" -f $n)
}

# ---- optional square minimap ----
if ($SquareRadar) {
  $rm = Join-Path $work 'v3_radar_mask.dds'
  & (Join-Path $tools 'make-square-radar.ps1') -Out $rm | Out-Null
  $staged['v3_radar_mask.dds'] = $rm
  Write-Output 'v3_radar_mask.dds  square'
}

# ---- pack everything together ----
$pak = Join-Path $work 'zz_aion2.pak'
if (Test-Path $pak) { Remove-Item $pak -Force }
$zs = [System.IO.File]::Open($pak, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
foreach ($k in $staged.Keys) {
  $e = $zip.CreateEntry($k, [System.IO.Compression.CompressionLevel]::Optimal)
  $w = $e.Open()
  $bytes = [System.IO.File]::ReadAllBytes($staged[$k])
  $w.Write($bytes, 0, $bytes.Length)
  $w.Dispose()
}
$zip.Dispose(); $zs.Dispose()
Write-Output ("packed {0} textures -> {1} ({2:N0} bytes)" -f $staged.Count, $pak, (Get-Item $pak).Length)

if ($Install) {
  $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'aion.bin' }
  if ($running) { Write-Output 'CLIENT IS RUNNING - close it and re-run'; exit 1 }
  foreach ($n in @('zz_skin.pak','zz_common.pak','zz_radar.pak','zz_calib.pak')) {
    $p = Join-Path $uiDir $n
    if (Test-Path $p) { Remove-Item $p -Force; Write-Output ("removed old " + $n) }
  }
  Copy-Item $pak $pakOut -Force
  Write-Output ("installed -> " + $pakOut)
}
