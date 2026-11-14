# make-template.ps1 -- turns the decoded UV map into working templates:
# one PNG per atlas, at real size, with every discovered sprite outlined and
# labelled. This is the underlay a designer paints over -- staying inside the
# rectangles keeps the result aligned with the UVs baked into the client's XML.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: make-template.ps1 -Csv <uvmap.csv> -OutDir <dir>

param(
  [Parameter(Mandatory = $true)][string]$Csv,
  [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @{
  'v5_common.dds'   = @(1024,1024); 'v4_common.dds'   = @(1024,1024)
  'v5_hud_s1.dds'   = @(1024,512);  'v4_hud_s1.dds'   = @(1024,512)
  'v4_popup1.dds'   = @(1024,1024); 'v5_common_1.dds' = @(1024,1024)
  'v5_common_2.dds' = @(1024,1024); 'v5_hud_s2.dds'   = @(1024,512)
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$rows = Import-Csv $Csv

foreach ($grp in ($rows | Group-Object File)) {
  $name = $grp.Name
  if (-not $sizes.ContainsKey($name)) { continue }
  $W = $sizes[$name][0]; $H = $sizes[$name][1]

  $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 26, 28, 32))
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::SingleBitPerPixelGridFit

  # faint 64px reference grid
  $grid = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(38, 255, 255, 255), 1)
  for ($i = 64; $i -lt $W; $i += 64) { $g.DrawLine($grid, $i, 0, $i, $H) }
  for ($i = 64; $i -lt $H; $i += 64) { $g.DrawLine($grid, 0, $i, $W, $i) }
  $grid.Dispose()

  # Drop sparse groups: a real sprite fills its bounding box almost completely.
  # Stretched 9-slice areas produce scattered pixels that share a (dx,dy) by
  # chance and would otherwise merge into a huge bogus rectangle.
  $dense = $grp.Group | Where-Object {
    $area = [int]$_.W * [int]$_.H
    $area -gt 0 -and ([double][int]$_.Pixels / $area) -ge 0.55
  }
  $dropped = $grp.Group.Count - $dense.Count
  if ($dropped -gt 0) { Write-Output ("  {0}: dropped {1} sparse groups" -f $name, $dropped) }

  # bigger sprites first, so small ones stay legible on top
  $sorted = $dense | Sort-Object { [int]$_.W * [int]$_.H } -Descending
  $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(48, 90, 200, 255))
  $pen  = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 120, 220, 255), 1)
  $font = New-Object System.Drawing.Font('Consolas', 9, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

  $n = 0
  foreach ($r in $sorted) {
    $x = [int]$r.AtlasX; $y = [int]$r.AtlasY; $w = [int]$r.W; $h = [int]$r.H
    if ($w -lt 3 -or $h -lt 3) { continue }
    $g.FillRectangle($fill, $x, $y, $w, $h)
    $g.DrawRectangle($pen, $x, $y, ($w - 1), ($h - 1))
    if ($w -ge 34 -and $h -ge 12) {
      $lbl = ("{0},{1}" -f $x, $y)
      $g.DrawString($lbl, $font, [System.Drawing.Brushes]::Black, ($x + 3), ($y + 2))
      $g.DrawString($lbl, $font, [System.Drawing.Brushes]::White, ($x + 2), ($y + 1))
    }
    $n++
  }
  $fill.Dispose(); $pen.Dispose(); $font.Dispose()

  $hdrFont = New-Object System.Drawing.Font('Consolas', 18, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
  $g.DrawString(("{0}   {1}x{2}   {3} sprites" -f $name, $W, $H, $n), $hdrFont, [System.Drawing.Brushes]::White, 8, 6)
  $hdrFont.Dispose()
  $g.Dispose()

  $out = Join-Path $OutDir ($name.Replace('.dds', '') + '_template.png')
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output ("{0,-18} {1} sprites -> {2}" -f $name, $n, $out)
}
