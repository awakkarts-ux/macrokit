# overlay-shot.ps1 -- draws the decoded sprite rectangles back onto the
# screenshot they came from, labelled with their atlas coordinates.
#
# The UV map gives coordinates but not meaning. Seeing the rectangles in place
# on the real UI is what tells us which one is the HP bar and which is a window
# corner -- that mapping is the missing half before anything can be redrawn.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: overlay-shot.ps1 -Shot <png> -Out <png> [-MinArea 200] [-Crop "x,y,w,h"]

param(
  [Parameter(Mandatory = $true)][string]$Shot,
  [Parameter(Mandatory = $true)][string]$Out,
  [int]$MinArea = 200,
  [string]$Crop = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$names = @('v4_common','v4_common_1','v4_hud_s1','v4_hud_s2','v4_popup1','v4_popup2','v4_popup3','map_default')

$code = @'
using System;
using System.Collections.Generic;
public class UvShot {
    public class R {
        public int File, Sx0, Sy0, Sx1, Sy1, Ax0, Ay0, Ax1, Ay1, Count;
    }
    public static List<R> Decode(byte[] px, int w, int h, int stride, int minArea) {
        var map = new Dictionary<long, R>();
        for (int y = 0; y < h - 1; y++) {
            int row = y * stride;
            for (int x = 0; x < w - 1; x++) {
                int o = row + x * 4;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                if (b < 128) continue;
                int code = b - 128; int file = code >> 4;
                if (file > 7) continue;
                int ax = ((code >> 2) & 3) * 256 + r;
                int ay = (code & 3) * 256 + g;
                int o2 = o + 4, o3 = o + stride;
                if (px[o2] != b || px[o2 + 2] != (byte)((r + 1) & 0xFF) || px[o2 + 1] != g) continue;
                if (px[o3] != b || px[o3 + 2] != r || px[o3 + 1] != (byte)((g + 1) & 0xFF)) continue;
                int dx = x - ax, dy = y - ay;
                long key = ((long)file << 40) ^ ((long)(dx + 4096) << 20) ^ (long)(dy + 4096);
                R reg;
                if (!map.TryGetValue(key, out reg)) {
                    reg = new R { File = file, Sx0 = x, Sy0 = y, Sx1 = x, Sy1 = y,
                                  Ax0 = ax, Ay0 = ay, Ax1 = ax, Ay1 = ay };
                    map[key] = reg;
                }
                if (x < reg.Sx0) reg.Sx0 = x; if (y < reg.Sy0) reg.Sy0 = y;
                if (x > reg.Sx1) reg.Sx1 = x; if (y > reg.Sy1) reg.Sy1 = y;
                if (ax < reg.Ax0) reg.Ax0 = ax; if (ay < reg.Ay0) reg.Ay0 = ay;
                if (ax > reg.Ax1) reg.Ax1 = ax; if (ay > reg.Ay1) reg.Ay1 = ay;
                reg.Count++;
            }
        }
        var outp = new List<R>();
        foreach (var r in map.Values) {
            int area = (r.Sx1 - r.Sx0 + 1) * (r.Sy1 - r.Sy0 + 1);
            // keep only dense groups; sparse ones are stretch artefacts
            if (r.Count >= minArea && area > 0 && (double)r.Count / area >= 0.55) outp.Add(r);
        }
        outp.Sort((a, b2) => b2.Count.CompareTo(a.Count));
        return outp;
    }
}
'@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

$bmp = New-Object System.Drawing.Bitmap($Shot)
$w = $bmp.Width; $h = $bmp.Height
$rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$buf = New-Object 'byte[]' ($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
$bmp.UnlockBits($data)

$regs = [UvShot]::Decode($buf, $w, $h, $stride, $MinArea)
Write-Output ("regions: {0}" -f $regs.Count)

$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
$fonts = New-Object System.Drawing.Font('Consolas', 10, [System.Drawing.FontStyle]::Bold)
# one colour per source atlas
$cols = @(
  [System.Drawing.Color]::FromArgb(255,255,90,90),  [System.Drawing.Color]::FromArgb(255,90,255,120),
  [System.Drawing.Color]::FromArgb(255,120,180,255),[System.Drawing.Color]::FromArgb(255,255,220,80),
  [System.Drawing.Color]::FromArgb(255,255,130,255),[System.Drawing.Color]::FromArgb(255,120,255,255),
  [System.Drawing.Color]::FromArgb(255,255,170,80), [System.Drawing.Color]::FromArgb(255,200,200,200)
)

foreach ($r in $regs) {
  $col = $cols[$r.File]
  $pen = New-Object System.Drawing.Pen($col, 1.0)
  $rw = $r.Sx1 - $r.Sx0; $rh = $r.Sy1 - $r.Sy0
  $g.DrawRectangle($pen, $r.Sx0, $r.Sy0, $rw, $rh)
  $pen.Dispose()
  if ($rw -ge 40 -and $rh -ge 14) {
    $lbl = "{0} {1},{2} {3}x{4}" -f $names[$r.File], $r.Ax0, $r.Ay0, ($r.Ax1 - $r.Ax0 + 1), ($r.Ay1 - $r.Ay0 + 1)
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(190, 0, 0, 0))
    $sz = $g.MeasureString($lbl, $fonts)
    $g.FillRectangle($bg, $r.Sx0, ($r.Sy0 - 13), $sz.Width, 13)
    $bg.Dispose()
    $fb = New-Object System.Drawing.SolidBrush($col)
    $g.DrawString($lbl, $fonts, $fb, $r.Sx0, ($r.Sy0 - 14))
    $fb.Dispose()
  }
}
$g.Dispose()

if ($Crop -ne '') {
  $p = $Crop.Split(',')
  $cr = New-Object System.Drawing.Rectangle([int]$p[0], [int]$p[1], [int]$p[2], [int]$p[3])
  $sub = $bmp.Clone($cr, $bmp.PixelFormat)
  $sub.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $sub.Dispose()
} else {
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
$bmp.Dispose()
Write-Output ("wrote " + $Out)

