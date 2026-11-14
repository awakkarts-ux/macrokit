# make-font-pak.ps1 -- builds a font override .pak for the Aion client.
#
# The client's own paks are ZIPs with bit-inverted 4-byte signatures and
# AES-OFB encrypted payloads. A plain ZIP (normal signatures, no encryption)
# renamed to .pak is read as-is -- that is what this script produces.
#
# The pak is mounted at the folder it sits in, so entries are bare file names
# and it must be dropped into <client>\l10n\<lang>\data\Fonts\.
#
# ASCII only: powershell.exe reads .ps1 without BOM as ANSI.
#
# Usage:
#   make-font-pak.ps1 -Source <dir-with-ttf> -Face Thicker-Regular-TRIAL.ttf
#   make-font-pak.ps1 -Source ... -Face ... -Install
#   make-font-pak.ps1 -Uninstall

param(
  [string]$Source,
  [string]$Face,
  [string]$Client = 'G:\Aion Empire\classic',
  [string]$Lang   = 'rus',
  [string]$Out    = '',
  [switch]$Install,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$fontsDir = Join-Path $Client ("l10n\{0}\data\Fonts" -f $Lang)
$target   = Join-Path $fontsDir 'fonts.pak'

if ($Uninstall) {
  if (Test-Path $target) { Remove-Item $target -Force; Write-Output ("removed " + $target) }
  else { Write-Output 'nothing to remove' }
  if ((Test-Path $fontsDir) -and -not (Get-ChildItem $fontsDir -Force)) {
    Remove-Item $fontsDir -Force
    Write-Output ("removed empty " + $fontsDir)
  }
  exit 0
}

if (-not $Source -or -not $Face) { Write-Output 'need -Source and -Face'; exit 1 }

$src = Join-Path $Source $Face
if (-not (Test-Path $src)) { Write-Output ("no such font: " + $src); exit 1 }

# Names the client asks for inside Fonts\. CHU_2002.ttf is the CJK face and is
# deliberately left alone -- a latin face there would knock out korean glyphs.
$names = @('CALIBRI.TTF', 'MYRIADPR.TTF', 'RomicCyr.ttf')

if ($Out -eq '') { $Out = Join-Path $env:TEMP 'fonts.pak' }
if (Test-Path $Out) { Remove-Item $Out -Force }

$bytes = [System.IO.File]::ReadAllBytes($src)
$zs = [System.IO.File]::Open($Out, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create)
foreach ($n in $names) {
  $e = $zip.CreateEntry($n, [System.IO.Compression.CompressionLevel]::Optimal)
  $w = $e.Open()
  $w.Write($bytes, 0, $bytes.Length)
  $w.Dispose()
}
$zip.Dispose()
$zs.Dispose()

$sz = (Get-Item $Out).Length
Write-Output ("built {0} ({1:N0} bytes) from {2}" -f $Out, $sz, $Face)
Write-Output ("entries: " + ($names -join ', '))

if ($Install) {
  if (-not (Test-Path $Client)) { Write-Output ("no client at " + $Client); exit 1 }
  if (-not (Test-Path $fontsDir)) { New-Item -ItemType Directory $fontsDir | Out-Null }
  Copy-Item $Out $target -Force
  Write-Output ("installed -> " + $target)
  Write-Output 'restart the client fully; roll back with -Uninstall'
}
