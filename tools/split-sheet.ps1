# split-sheet.ps1 -- cuts a generated sheet of UI elements into separate PNGs.
#
# Lets concepts be generated in batches instead of one asset at a time. Rather
# than trusting the generator to honour a grid, this keys out the flat backdrop
# and finds connected blobs, so elements may sit anywhere as long as they do not
# touch. Each blob is written out cropped to its own bounds, plus a numbered
# contact sheet for picking which piece goes where.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: split-sheet.ps1 -Png <sheet.png> -OutDir <dir> [-Threshold 40] [-MinSize 24]

param(
  [Parameter(Mandatory = $true)][string]$Png,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [int]$Threshold = 40,     # brightness below this counts as background
  [int]$MinSize = 24,       # ignore blobs smaller than this on either side
  [int]$Pad = 2
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Collections.Generic;

public class Blobs {
    public class Box { public int X0, Y0, X1, Y1, Count; }

    // Iterative flood fill over the foreground mask; recursion would blow the
    // stack on a 1024px sheet.
    public static List<Box> Find(byte[] px, int w, int h, int stride, int threshold) {
        var seen = new bool[w * h];
        var boxes = new List<Box>();
        var stack = new Stack<int>();

        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int idx = y * w + x;
                if (seen[idx]) continue;
                int o = y * stride + x * 4;
                int lum = (px[o] + px[o + 1] + px[o + 2]) / 3;
                if (lum <= threshold) { seen[idx] = true; continue; }

                var box = new Box { X0 = x, Y0 = y, X1 = x, Y1 = y, Count = 0 };
                stack.Push(idx);
                seen[idx] = true;
                while (stack.Count > 0) {
                    int cur = stack.Pop();
                    int cx = cur % w, cy = cur / w;
                    box.Count++;
                    if (cx < box.X0) box.X0 = cx;
                    if (cy < box.Y0) box.Y0 = cy;
                    if (cx > box.X1) box.X1 = cx;
                    if (cy > box.Y1) box.Y1 = cy;
                    for (int dy = -1; dy <= 1; dy++) {
                        for (int dx = -1; dx <= 1; dx++) {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int nidx = ny * w + nx;
                            if (seen[nidx]) continue;
                            int no = ny * stride + nx * 4;
                            int nlum = (px[no] + px[no + 1] + px[no + 2]) / 3;
                            if (nlum <= threshold) { seen[nidx] = true; continue; }
                            seen[nidx] = true;
                            stack.Push(nidx);
                        }
                    }
                }
                boxes.Add(box);
            }
        }
        return boxes;
    }
}
'@
Add-Type -TypeDefinition $code

$bmp = New-Object System.Drawing.Bitmap($Png)
$w = $bmp.Width; $h = $bmp.Height
$rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
$d = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $d.Stride
$buf = New-Object 'byte[]' ($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
$bmp.UnlockBits($d)
Write-Output ("sheet {0}x{1}" -f $w, $h)

$boxes = [Blobs]::Find($buf, $w, $h, $stride, $Threshold)
$keep = @($boxes | Where-Object { ($_.X1 - $_.X0) -ge $MinSize -and ($_.Y1 - $_.Y0) -ge $MinSize })
# reading order: top to bottom, then left to right
$keep = @($keep | Sort-Object { [int]($_.Y0 / 40) * 10000 + $_.X0 })
Write-Output ("blobs found: {0}, kept: {1}" -f $boxes.Count, $keep.Count)

New-Item -ItemType Directory -Force $OutDir | Out-Null
$i = 1
$cells = @()
foreach ($bx in $keep) {
  $x = [Math]::Max(0, $bx.X0 - $Pad); $y = [Math]::Max(0, $bx.Y0 - $Pad)
  $cw = [Math]::Min($w - $x, ($bx.X1 - $bx.X0 + 1 + $Pad * 2))
  $ch = [Math]::Min($h - $y, ($bx.Y1 - $bx.Y0 + 1 + $Pad * 2))
  $crop = $bmp.Clone((New-Object System.Drawing.Rectangle($x, $y, $cw, $ch)), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $name = ("{0:D2}_{1}x{2}.png" -f $i, $cw, $ch)
  $crop.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $cells += [pscustomobject]@{ N = $i; File = $name; W = $cw; H = $ch; Img = $crop }
  Write-Output ("  {0,2}  {1,4}x{2,-4}  {3}" -f $i, $cw, $ch, $name)
  $i++
}

# contact sheet so pieces can be referred to by number
if ($cells.Count -gt 0) {
  $cols = 4
  $cell = 220
  $rows = [Math]::Ceiling($cells.Count / $cols)
  $sheet = New-Object System.Drawing.Bitmap(($cols * $cell), ($rows * $cell))
  $g = [System.Drawing.Graphics]::FromImage($sheet)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 40, 44, 52))
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $f = New-Object System.Drawing.Font('Consolas', 12, [System.Drawing.FontStyle]::Bold)
  for ($k = 0; $k -lt $cells.Count; $k++) {
    $cx = ($k % $cols) * $cell; $cy = [int]($k / $cols) * $cell
    $im = $cells[$k].Img
    $sc = [Math]::Min(($cell - 40) / $im.Width, ($cell - 40) / $im.Height)
    $dw = [int]($im.Width * $sc); $dh = [int]($im.Height * $sc)
    $g.DrawImage($im, ($cx + ($cell - $dw) / 2), ($cy + 26 + ($cell - 40 - $dh) / 2), $dw, $dh)
    $g.DrawString(("{0}  {1}x{2}" -f $cells[$k].N, $cells[$k].W, $cells[$k].H), $f, [System.Drawing.Brushes]::White, ($cx + 8), ($cy + 6))
    $im.Dispose()
  }
  $g.Dispose()
  $cs = Join-Path $OutDir 'contact-sheet.png'
  $sheet.Save($cs, [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
  Write-Output ("contact sheet -> " + $cs)
}
$bmp.Dispose()
