# ui-style-preview.ps1 -- draws a style board for the Aion UI reskin.
#
# The target look (Mobile Legends lobby) is procedural rather than painted:
# translucent panels, flat gradients, hairline rim light, chamfered corners,
# gold used only as an accent. All of that is cheaper and far more consistent
# to draw in code than to generate, which is why no image model is involved.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: ui-style-preview.ps1 -Out board.png

param([Parameter(Mandatory = $true)][string]$Out)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function C([int]$a, [string]$hex) {
  $r = [Convert]::ToInt32($hex.Substring(0,2),16)
  $g = [Convert]::ToInt32($hex.Substring(2,2),16)
  $b = [Convert]::ToInt32($hex.Substring(4,2),16)
  return [System.Drawing.Color]::FromArgb($a, $r, $g, $b)
}

# palette lifted from the reference lobby
$NAVY_DK = '10143A'; $NAVY    = '232C6E'; $NAVY_LT = '3A47A8'
$RIM     = 'A8BCFF'; $RIM_HOT = 'DCE6FF'
$GOLD_DK = 'B8862F'; $GOLD    = 'E8BC5A'; $GOLD_LT = 'FCE6A8'
$HP_DK   = 'B4243A'; $HP      = 'F0475A'; $HP_LT   = 'FF9A90'
$MP_DK   = '1F6FA8'; $MP      = '3AA8F0'; $MP_LT   = '9FE2FF'

# Chamfered rectangle: the reference cuts corners instead of rounding them.
function ChamferPath($x, $y, $w, $h, $cut) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $pts = New-Object 'System.Drawing.PointF[]' 8
  $pts[0] = New-Object System.Drawing.PointF(($x + $cut), $y)
  $pts[1] = New-Object System.Drawing.PointF(($x + $w - $cut), $y)
  $pts[2] = New-Object System.Drawing.PointF(($x + $w), ($y + $cut))
  $pts[3] = New-Object System.Drawing.PointF(($x + $w), ($y + $h - $cut))
  $pts[4] = New-Object System.Drawing.PointF(($x + $w - $cut), ($y + $h))
  $pts[5] = New-Object System.Drawing.PointF(($x + $cut), ($y + $h))
  $pts[6] = New-Object System.Drawing.PointF($x, ($y + $h - $cut))
  $pts[7] = New-Object System.Drawing.PointF($x, ($y + $cut))
  $p.AddPolygon($pts)
  return $p
}

function FillGrad($g, $path, $x, $y, $w, $h, $c1, $c2) {
  $r = New-Object System.Drawing.Rectangle($x, $y, [Math]::Max($w,1), [Math]::Max($h,1))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r, $c1, $c2, 90.0)
  $g.FillPath($br, $path)
  $br.Dispose()
}

# --- board ---------------------------------------------------------------
$W = 980; $H = 620
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

# scene-like backdrop so translucency is visible
$bgRect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$bgBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect, (C 255 '4A3A5E'), (C 255 '20304A'), 25.0)
$g.FillRectangle($bgBr, $bgRect); $bgBr.Dispose()
$rnd = New-Object System.Random 7
for ($i = 0; $i -lt 90; $i++) {
  $bx = $rnd.Next(0, $W); $by = $rnd.Next(0, $H); $bs = $rnd.Next(20, 90)
  $bb = New-Object System.Drawing.SolidBrush((C 12 'FFFFFF'))
  $g.FillEllipse($bb, $bx, $by, $bs, $bs); $bb.Dispose()
}

$lbl = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Regular)
$lblBr = New-Object System.Drawing.SolidBrush((C 200 'DCE6FF'))

function Label($g, $t, $x, $y) { $g.DrawString($t, $lbl, $lblBr, $x, $y) }

# --- 1. window panel -----------------------------------------------------
Label $g 'Window panel' 40 22
$p = ChamferPath 40 46 400 200 14
FillGrad $g $p 40 46 400 200 (C 214 $NAVY) (C 236 $NAVY_DK)
$pen = New-Object System.Drawing.Pen((C 190 $RIM), 1.4); $g.DrawPath($pen, $p); $pen.Dispose()
# title strip
$t = ChamferPath 40 46 400 40 14
FillGrad $g $t 40 46 400 40 (C 236 $NAVY_LT) (C 214 $NAVY)
$pen = New-Object System.Drawing.Pen((C 120 $RIM), 1.0); $g.DrawPath($pen, $t); $pen.Dispose()
$t.Dispose()
# gold accent hairline under the title
$gp = New-Object System.Drawing.Pen((C 200 $GOLD), 1.6)
$g.DrawLine($gp, 54, 87, 426, 87); $gp.Dispose()
$p.Dispose()

# --- 2. progress bars ----------------------------------------------------
Label $g 'HP & MP bars' 40 268
$barY = 292
foreach ($set in @(@($HP_DK, $HP, $HP_LT, 0.72), @($MP_DK, $MP, $MP_LT, 0.45))) {
  $trough = ChamferPath 40 $barY 400 26 8
  FillGrad $g $trough 40 $barY 400 26 (C 235 '0B0F26') (C 235 '18204A')
  $pen = New-Object System.Drawing.Pen((C 150 $RIM), 1.2); $g.DrawPath($pen, $trough); $pen.Dispose()
  $fw = [int](394 * $set[3])
  $fill = ChamferPath 43 ($barY + 3) $fw 20 6
  FillGrad $g $fill 43 ($barY + 3) $fw 20 (C 255 $set[2]) (C 255 $set[0])
  # hot core line along the top of the fill
  $hp = New-Object System.Drawing.Pen((C 190 $set[2]), 1.4)
  $g.DrawLine($hp, 47, ($barY + 6), (43 + $fw - 5), ($barY + 6)); $hp.Dispose()
  $fill.Dispose(); $trough.Dispose()
  $barY += 40
}

# --- 3. inventory slots --------------------------------------------------
Label $g 'Slots' 40 380
for ($i = 0; $i -lt 5; $i++) {
  $sx = 40 + $i * 62
  $s = ChamferPath $sx 404 54 54 10
  FillGrad $g $s $sx 404 54 54 (C 200 '141A3C') (C 220 '0A0E24')
  $pen = New-Object System.Drawing.Pen((C 150 $RIM), 1.2); $g.DrawPath($pen, $s); $pen.Dispose()
  if ($i -eq 4) {  # selected state: gold accent
    $pen = New-Object System.Drawing.Pen((C 230 $GOLD), 1.8); $g.DrawPath($pen, $s); $pen.Dispose()
  }
  $s.Dispose()
}

# --- 4. buttons ----------------------------------------------------------
Label $g 'Buttons' 40 480
$states = @(
  @('Normal', (C 226 $NAVY_LT), (C 236 $NAVY),    (C 170 $RIM)),
  @('Hover', (C 240 '5468C8'), (C 240 $NAVY_LT), (C 230 $RIM_HOT)),
  @('Accent',  (C 240 $GOLD),    (C 240 $GOLD_DK), (C 240 $GOLD_LT))
)
for ($i = 0; $i -lt 3; $i++) {
  $bx = 40 + $i * 130
  $b = ChamferPath $bx 504 118 40 10
  FillGrad $g $b $bx 504 118 40 $states[$i][1] $states[$i][2]
  $pen = New-Object System.Drawing.Pen($states[$i][3], 1.4); $g.DrawPath($pen, $b); $pen.Dispose()
  $f = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
  $sf = New-Object System.Drawing.StringFormat
  $sf.Alignment = [System.Drawing.StringAlignment]::Center
  $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
  $rr = New-Object System.Drawing.RectangleF($bx, 504, 118, 40)
  $tc = if ($i -eq 2) { (C 255 '2A1C06') } else { (C 245 'EAF0FF') }
  $tb = New-Object System.Drawing.SolidBrush($tc)
  $g.DrawString($states[$i][0], $f, $tb, $rr, $sf)
  $tb.Dispose(); $f.Dispose(); $b.Dispose()
}

# --- 5. right-hand list card (as in the reference) -----------------------
Label $g 'List card' 520 22
$card = ChamferPath 520 46 420 200 14
FillGrad $g $card 520 46 420 200 (C 200 $NAVY) (C 226 $NAVY_DK)
$pen = New-Object System.Drawing.Pen((C 170 $RIM), 1.4); $g.DrawPath($pen, $card); $pen.Dispose()
$card.Dispose()
for ($i = 0; $i -lt 3; $i++) {
  $ry = 60 + $i * 62
  $av = ChamferPath 534 $ry 50 50 9
  FillGrad $g $av 534 $ry 50 50 (C 235 $NAVY_LT) (C 235 '141A3C')
  $pen = New-Object System.Drawing.Pen((C 210 $GOLD), 1.5); $g.DrawPath($pen, $av); $pen.Dispose()
  $av.Dispose()
  $sep = New-Object System.Drawing.Pen((C 60 $RIM), 1.0)
  $g.DrawLine($sep, 596, ($ry + 54), 926, ($ry + 54)); $sep.Dispose()
  $nb = New-Object System.Drawing.SolidBrush((C 230 'DCE6FF'))
  $nf = New-Object System.Drawing.Font('Segoe UI Semibold', 11)
  $g.DrawString('Player ' + ($i + 1), $nf, $nb, 598, ($ry + 6))
  $nb.Dispose(); $nf.Dispose()
}

# --- 6. quickslot row ----------------------------------------------------
Label $g 'Quickslots' 520 268
for ($i = 0; $i -lt 6; $i++) {
  $qx = 520 + $i * 58
  $q = ChamferPath $qx 292 50 50 9
  FillGrad $g $q $qx 292 50 50 (C 190 '18204A') (C 215 '0A0E24')
  $pen = New-Object System.Drawing.Pen((C 140 $RIM), 1.2); $g.DrawPath($pen, $q); $pen.Dispose()
  $q.Dispose()
}

# --- 7. scrollbar + tabs -------------------------------------------------
Label $g 'Tabs + scrollbar' 520 380
for ($i = 0; $i -lt 3; $i++) {
  $tx = 520 + $i * 108
  $tb2 = ChamferPath $tx 404 100 34 9
  if ($i -eq 0) { FillGrad $g $tb2 $tx 404 100 34 (C 236 $NAVY_LT) (C 236 $NAVY) }
  else { FillGrad $g $tb2 $tx 404 100 34 (C 150 '141A3C') (C 180 '0A0E24') }
  $pen = New-Object System.Drawing.Pen((C 150 $RIM), 1.2); $g.DrawPath($pen, $tb2); $pen.Dispose()
  $tb2.Dispose()
}
$track = ChamferPath 900 452 14 120 5
FillGrad $g $track 900 452 14 120 (C 190 '0A0E24') (C 190 '141A3C')
$pen = New-Object System.Drawing.Pen((C 90 $RIM), 1.0); $g.DrawPath($pen, $track); $pen.Dispose(); $track.Dispose()
$thumb = ChamferPath 901 460 12 54 5
FillGrad $g $thumb 901 460 12 54 (C 235 $NAVY_LT) (C 235 $NAVY)
$pen = New-Object System.Drawing.Pen((C 190 $RIM), 1.0); $g.DrawPath($pen, $thumb); $pen.Dispose(); $thumb.Dispose()

$lbl.Dispose(); $lblBr.Dispose()
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("wrote " + $Out)
