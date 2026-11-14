# read-uvmap.ps1 -- decodes screenshots taken with the UV-probe atlases and
# reports, for every UI sprite on screen, its exact rectangle inside the atlas.
#
# The probe encodes each atlas pixel as R = x & 0xFF, G = y & 0xFF,
# B = 128 + (fileIndex << 4) + ((x >> 8) << 2) + (y >> 8).
#
# A sprite is blitted 1:1, so across its whole area the difference between
# screen and atlas coordinates is constant. Grouping pixels by
# (fileIndex, screenX - atlasX, screenY - atlasY) therefore separates sprites.
# Stretched (9-slice) regions break into several groups, which is fine.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: read-uvmap.ps1 -Shots <dir-or-file> [-MinArea 64] [-Csv out.csv]

param(
  [Parameter(Mandatory = $true)][string]$Shots,
  [int]$MinArea = 64,
  [string]$Csv = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$names = @('v4_common','v4_common_1','v4_hud_s1','v4_hud_s2','v4_popup1','v4_popup2','v4_popup3','map_default')

$code = @'
using System;
using System.Collections.Generic;

public class UvDecode {
    public class Region {
        public int File, Dx, Dy;
        public int Sx0, Sy0, Sx1, Sy1;   // screen bounds
        public int Ax0, Ay0, Ax1, Ay1;   // atlas bounds
        public int Count;
    }

    // A pixel is accepted only if its right and lower neighbours continue the
    // gradient; that rejects scenery which happens to land in the value range.
    public static List<Region> Decode(byte[] px, int w, int h, int stride, int minArea) {
        var map = new Dictionary<long, Region>();

        for (int y = 0; y < h - 1; y++) {
            int row = y * stride;
            for (int x = 0; x < w - 1; x++) {
                int o = row + x * 4;          // B,G,R,A
                int b = px[o], g = px[o + 1], r = px[o + 2];
                if (b < 128) continue;
                int code = b - 128;
                int file = code >> 4;
                if (file > 7) continue;
                int ax = ((code >> 2) & 3) * 256 + r;
                int ay = (code & 3) * 256 + g;

                // right neighbour must be ax+1 with same file/row
                int o2 = o + 4;
                if (px[o2] != b) continue;
                if (px[o2 + 2] != (byte)((r + 1) & 0xFF)) continue;
                if (px[o2 + 1] != g) continue;
                // lower neighbour must be ay+1
                int o3 = o + stride;
                if (px[o3] != b) continue;
                if (px[o3 + 2] != r) continue;
                if (px[o3 + 1] != (byte)((g + 1) & 0xFF)) continue;

                int dx = x - ax, dy = y - ay;
                long key = ((long)file << 40) ^ ((long)(dx + 4096) << 20) ^ (long)(dy + 4096);
                Region reg;
                if (!map.TryGetValue(key, out reg)) {
                    reg = new Region { File = file, Dx = dx, Dy = dy,
                                       Sx0 = x, Sy0 = y, Sx1 = x, Sy1 = y,
                                       Ax0 = ax, Ay0 = ay, Ax1 = ax, Ay1 = ay, Count = 0 };
                    map[key] = reg;
                }
                if (x < reg.Sx0) reg.Sx0 = x;
                if (y < reg.Sy0) reg.Sy0 = y;
                if (x > reg.Sx1) reg.Sx1 = x;
                if (y > reg.Sy1) reg.Sy1 = y;
                if (ax < reg.Ax0) reg.Ax0 = ax;
                if (ay < reg.Ay0) reg.Ay0 = ay;
                if (ax > reg.Ax1) reg.Ax1 = ax;
                if (ay > reg.Ay1) reg.Ay1 = ay;
                reg.Count++;
            }
        }

        var outp = new List<Region>();
        foreach (var r in map.Values) if (r.Count >= minArea) outp.Add(r);
        outp.Sort((a, b2) => b2.Count.CompareTo(a.Count));
        return outp;
    }
}
'@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

$files = @()
if (Test-Path $Shots -PathType Container) {
  $files = Get-ChildItem $Shots -Filter '*.png' | Select-Object -ExpandProperty FullName
} else { $files = @($Shots) }

$all = @{}
foreach ($f in $files) {
  $bmp = New-Object System.Drawing.Bitmap($f)
  $w = $bmp.Width; $h = $bmp.Height
  $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $data.Stride
  $buf = New-Object 'byte[]' ($stride * $h)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($data)
  $bmp.Dispose()

  $regs = [UvDecode]::Decode($buf, $w, $h, $stride, $MinArea)
  Write-Output ("{0,-42} {1} regions" -f (Split-Path $f -Leaf), $regs.Count)
  foreach ($r in $regs) {
    $key = "{0}|{1},{2},{3},{4}" -f $r.File, $r.Ax0, $r.Ay0, $r.Ax1, $r.Ay1
    if (-not $all.ContainsKey($key)) {
      $all[$key] = [pscustomobject]@{
        File   = $names[$r.File]
        AtlasX = $r.Ax0; AtlasY = $r.Ay0
        W      = ($r.Ax1 - $r.Ax0 + 1); H = ($r.Ay1 - $r.Ay0 + 1)
        ScreenX = $r.Sx0; ScreenY = $r.Sy0
        Pixels = $r.Count
      }
    }
  }
}

Write-Output ''
Write-Output ("=== distinct sprite regions: {0}" -f $all.Count)
$rows = $all.Values | Sort-Object Pixels -Descending
$rows | Select-Object -First 60 | Format-Table -AutoSize
if ($Csv -ne '') { $rows | Export-Csv -Path $Csv -NoTypeInformation -Encoding UTF8; Write-Output ("csv -> " + $Csv) }

