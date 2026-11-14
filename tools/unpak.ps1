# unpak.ps1 -- decrypts and extracts entries from Aion .pak archives.
#
# How this works, and why no key derivation is needed:
#
#   * A .pak is a ZIP whose 4-byte signatures are bit-inverted.
#   * Only the FIRST 32 BYTES of each entry's payload are encrypted; everything
#     past that is stored in the clear (verified: a stored .dds shows its DXT
#     fourCC at offset 84 unencrypted).
#   * The cipher is a plain XOR against a 32-byte slice of a static
#     pseudo-random table compiled into bin64\crysystem.dll. Recovered
#     keystreams were found verbatim inside that DLL, which is what gave the
#     whole thing away.
#
# Rather than reverse how the client picks the slice offset, we simply try every
# offset in the table and keep the one that yields a valid entry -- a deflate
# stream that inflates to exactly the declared size, or, for stored entries, the
# expected file magic. The table is a few kilobytes, so this is instant.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage:
#   unpak.ps1 -Pak <file.pak> -Out <dir> [-Filter <regex>] [-List]

param(
  [Parameter(Mandatory = $true)][string]$Pak,
  [string]$Out = '',
  [string]$Filter = '',
  [string]$Dll = 'G:\Aion Empire\classic\bin64\crysystem.dll',
  [int]$TableStart = 0x21C800,
  [int]$TableLen = 0x2000,
  [switch]$List
)

$ErrorActionPreference = 'Stop'

# ---- load the keystream table ------------------------------------------
$fsD = [System.IO.File]::OpenRead($Dll)
$fsD.Position = $TableStart
$table = New-Object 'byte[]' $TableLen
[void]$fsD.Read($table, 0, $TableLen)
$fsD.Close()

# ---- read the pak's central directory -----------------------------------
$fs = [System.IO.File]::OpenRead($Pak)
$br = New-Object System.IO.BinaryReader($fs)
$tailLen = [int][Math]::Min(70000, $fs.Length)
$fs.Position = $fs.Length - $tailLen
$tail = $br.ReadBytes($tailLen)

$eocd = [byte[]](0xAF,0xB4,0xFA,0xF9)
$pos = -1
for ($i = $tail.Length - 22; $i -ge 0; $i--) {
  if ($tail[$i] -eq $eocd[0] -and $tail[$i+1] -eq $eocd[1] -and $tail[$i+2] -eq $eocd[2] -and $tail[$i+3] -eq $eocd[3]) { $pos = $i; break }
}
if ($pos -lt 0) { Write-Output 'EOCD not found'; exit 1 }
$cdSize = [BitConverter]::ToUInt32($tail, $pos + 12)
$cdOff  = [BitConverter]::ToUInt32($tail, $pos + 16)
$fs.Position = $cdOff
$cd = $br.ReadBytes([int]$cdSize)

if ($Out -ne '') { New-Item -ItemType Directory -Force $Out | Out-Null }

$cdSig = [byte[]](0xAF,0xB4,0xFE,0xFD)
$p = 0
$okCount = 0; $failCount = 0
$knownOffset = -1        # once found, try it first: entries in a pak share it

while ($p + 46 -le $cd.Length) {
  if (-not ($cd[$p] -eq $cdSig[0] -and $cd[$p+1] -eq $cdSig[1] -and $cd[$p+2] -eq $cdSig[2] -and $cd[$p+3] -eq $cdSig[3])) { break }
  $method  = [BitConverter]::ToUInt16($cd, $p + 10)
  $compSz  = [BitConverter]::ToUInt32($cd, $p + 20)
  $uncSz   = [BitConverter]::ToUInt32($cd, $p + 24)
  $nameLen = [BitConverter]::ToUInt16($cd, $p + 28)
  $extraLen= [BitConverter]::ToUInt16($cd, $p + 30)
  $cmtLen  = [BitConverter]::ToUInt16($cd, $p + 32)
  $lhOff   = [BitConverter]::ToUInt32($cd, $p + 42)
  $name    = [System.Text.Encoding]::GetEncoding(1251).GetString($cd, $p + 46, $nameLen)
  $p += 46 + $nameLen + $extraLen + $cmtLen

  if ($Filter -ne '' -and $name -notmatch $Filter) { continue }
  if ($List) { Write-Output ("{0,10} {1,10} m{2}  {3}" -f $compSz, $uncSz, $method, $name); continue }
  if ($compSz -eq 0) { continue }

  $fs.Position = $lhOff
  $lh = $br.ReadBytes(30)
  $lnLen = [BitConverter]::ToUInt16($lh, 26)
  $leLen = [BitConverter]::ToUInt16($lh, 28)
  $fs.Position = $lhOff + 30 + $lnLen + $leLen
  $blob = $br.ReadBytes([int]$compSz)

  # candidate offsets: the one that already worked, then the whole table
  $order = New-Object System.Collections.Generic.List[int]
  if ($knownOffset -ge 0) { $order.Add($knownOffset) }
  for ($o = 0; $o -le $table.Length - 32; $o++) { if ($o -ne $knownOffset) { $order.Add($o) } }

  $result = $null
  foreach ($o in $order) {
    $t = New-Object 'byte[]' $blob.Length
    [Array]::Copy($blob, $t, $blob.Length)
    $n = [Math]::Min(32, $t.Length)
    for ($i = 0; $i -lt $n; $i++) { $t[$i] = $t[$i] -bxor $table[$o + $i] }

    if ($method -eq 0) {
      # stored: accept on a recognisable magic
      if ($t.Length -ge 4 -and (
          ($t[0] -eq 0x44 -and $t[1] -eq 0x44 -and $t[2] -eq 0x53 -and $t[3] -eq 0x20) -or   # DDS
          ($t[0] -eq 0x4F -and $t[1] -eq 0x67 -and $t[2] -eq 0x67 -and $t[3] -eq 0x53) -or   # OggS
          ($t[0] -eq 0x3C) -or ($t[0] -eq 0xEF -and $t[1] -eq 0xBB))) {                      # xml / bom
        $result = $t; $knownOffset = $o; break
      }
    } else {
      try {
        $ms = New-Object System.IO.MemoryStream(,$t)
        $ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
        $o2 = New-Object System.IO.MemoryStream
        $ds.CopyTo($o2); $ds.Dispose()
        $bytes = $o2.ToArray()
        # a truncated inflate does not throw, so demand the exact declared size
        if ($bytes.Length -eq $uncSz) { $result = $bytes; $knownOffset = $o; break }
      } catch { }
    }
  }

  if ($result -eq $null) {
    Write-Output ("FAIL  {0}" -f $name)
    $failCount++
    continue
  }
  $okCount++
  if ($Out -ne '') {
    $dest = Join-Path $Out ($name -replace '/', '\')
    $dir = Split-Path $dest -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [System.IO.File]::WriteAllBytes($dest, $result)
  }
  Write-Output ("OK    {0,-46} {1,9} bytes  keyOffset=0x{2:X}" -f $name, $result.Length, $knownOffset)
}
$br.Close(); $fs.Close()
if (-not $List) { Write-Output ("extracted {0}, failed {1}" -f $okCount, $failCount) }
