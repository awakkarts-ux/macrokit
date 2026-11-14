# ui-style-aion2.ps1 -- style board in the Aion 2 idiom.
#
# Reference reads as restrained and dark: near-black translucent panels,
# borders barely present (one pixel, low contrast), separation by tone rather
# than by frame, teal used for anything interactive, gold only in micro doses
# (a chevron over a card, a slider diamond) plus one bright CTA per screen,
# and glow instead of outline to mark the active tab.
#
# Deliberately no painted texture anywhere -- the whole language is gradients,
# hairlines and glows, which is why this is drawn rather than generated. It
# also survives Aion Classic's small scale, where ornament would turn to mush.
#
# ASCII only: powershell.exe reads a BOM-less .ps1 as ANSI.
#
# Usage: ui-style-aion2.ps1 -Out board.png

param([Parameter(Mandatory = $true)][string]$Out)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function C([int]$a, [string]$hex) {
  return [System.Drawing.Color]::FromArgb($a,
    [Convert]::ToInt32($hex.Substring(0,2),16),
    [Convert]::ToInt32($hex.Substring(2,2),16),
    [Convert]::ToInt32($hex.Substring(4,2),16))
}

# palette read off the reference
$PANEL_TOP = '141A26'; $PANEL_BOT = '080B12'
$RAISED    = '1C2432'; $LINE      = '2E3849'; $LINE_HI = '4A5A72'
$TEAL      = '4FD8E8'; $TEAL_DIM  = '2A7F8C'
$GOLD      = 'C9A227'; $GOLD_LT   = 'F0DCA0'
$CTA_A     = 'F2D27A'; $CTA_B     = 'C9992E'
$TXT       = 'E4EAF2'; $TXT_DIM   = '7C8798'
$HP        = 'D6394A'; $MP        = '2E9BD6'

function RoundPath($x, $y, $w, $h, $r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
  $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
  $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

function FillV($g, $path, $x, $y, $w, $h, $c1, $c2) {
  $r = New-Object System.Drawing.Rectangle($x, ($y - 1), ([Math]::Max($w,1)), ([Math]::Max($h,1) + 2))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r, $c1, $c2, 90.0)
  $g.FillPath($br, $path); $br.Dispose()
}

# soft glow: stack translucent strokes outward
function Glow($g, $path, $col, $passes) {
  for ($i = $passes; $i -ge 1; $i--) {
    $a = [int](40 / $i)
    $pen = New-Object System.Drawing.Pen((C $a $col), ($i * 2.0))
    $g.DrawPath($pen, $path); $pen.Dispose()
  }
}

$W = 1000; $H = 660
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

# backdrop: a lit scene, so panel translucency reads
$bgR = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$bgB = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgR, (C 255 '3A3630'), (C 255 '10141C'), 60.0)
$g.FillRectangle($bgB, $bgR); $bgB.Dispose()
$rnd = New-Object System.Random 11
for ($i = 0; $i -lt 60; $i++) {
  $b = New-Object System.Drawing.SolidBrush((C 10 'FFD9A0'))
  $g.FillEllipse($b, $rnd.Next(0,$W), $rnd.Next(0,$H), $rnd.Next(30,140), $rnd.Next(30,140)); $b.Dispose()
}

$fLbl  = New-Object System.Drawing.Font('Segoe UI', 10)
$fTxt  = New-Object System.Drawing.Font('Segoe UI', 10)
$fHead = New-Object System.Drawing.Font('Segoe UI Light', 15)
$fTab  = New-Object System.Drawing.Font('Segoe UI', 11)
$brLbl = New-Object System.Drawing.SolidBrush((C 150 $TXT_DIM))
$brTxt = New-Object System.Drawing.SolidBrush((C 235 $TXT))
$brDim = New-Object System.Drawing.SolidBrush((C 150 $TXT_DIM))

# ============ 1. window with tab row =====================================
$g.DrawString('Window + tabs', $fLbl, $brLbl, 34, 18)
$p = RoundPath 34 40 470 250 4
FillV $g $p 34 40 470 250 (C 232 $PANEL_TOP) (C 244 $PANEL_BOT)
$pen = New-Object System.Drawing.Pen((C 110 $LINE), 1.0); $g.DrawPath($pen, $p); $pen.Dispose()
$p.Dispose()
$g.DrawString('Customize', $fHead, $brTxt, 50, 52)

# tab row: active one carries a glow underline, not a frame
$tabs = @('Preset','Face','Hair','Body')
$tx = 60
for ($i = 0; $i -lt 4; $i++) {
  $act = ($i -eq 1)
  $b = if ($act) { $brTxt } else { $brDim }
  $g.DrawString($tabs[$i], $fTab, $b, $tx, 92)
  $tw = [int]$g.MeasureString($tabs[$i], $fTab).Width
  if ($act) {
    $u = New-Object System.Drawing.Drawing2D.GraphicsPath
    $u.AddLine($tx, 116, ($tx + $tw), 116)
    Glow $g $u 'FFFFFF' 4
    $pen = New-Object System.Drawing.Pen((C 235 'FFFFFF'), 1.4); $g.DrawPath($pen, $u); $pen.Dispose()
    $u.Dispose()
  }
  $tx += $tw + 34
}
$sep = New-Object System.Drawing.Pen((C 70 $LINE), 1.0); $g.DrawLine($sep, 50, 126, 488, 126); $sep.Dispose()

# sliders: hairline track, teal fill, diamond handle
$sy = 150
foreach ($s in @(@('Skin gloss', 0.62), @('Eye rotation', 0.30), @('Brow height', 0.78))) {
  $g.DrawString($s[0], $fTxt, $brDim, 52, ($sy - 16))
  $tr = New-Object System.Drawing.Pen((C 120 $LINE), 2.0)
  $g.DrawLine($tr, 52, $sy, 420, $sy); $tr.Dispose()
  $fx = 52 + [int](368 * $s[1])
  $fp = New-Object System.Drawing.Drawing2D.GraphicsPath
  $fp.AddLine(52, $sy, $fx, $sy)
  Glow $g $fp $TEAL 3
  $pen = New-Object System.Drawing.Pen((C 240 $TEAL), 2.0); $g.DrawPath($pen, $fp); $pen.Dispose(); $fp.Dispose()
  $dm = New-Object System.Drawing.Drawing2D.GraphicsPath
  $pts = New-Object 'System.Drawing.PointF[]' 4
  $pts[0] = New-Object System.Drawing.PointF($fx, ($sy - 7))
  $pts[1] = New-Object System.Drawing.PointF(($fx + 7), $sy)
  $pts[2] = New-Object System.Drawing.PointF($fx, ($sy + 7))
  $pts[3] = New-Object System.Drawing.PointF(($fx - 7), $sy)
  $dm.AddPolygon($pts)
  $br = New-Object System.Drawing.SolidBrush((C 255 $TEAL)); $g.FillPath($br, $dm); $br.Dispose()
  $pen = New-Object System.Drawing.Pen((C 200 'FFFFFF'), 1.0); $g.DrawPath($pen, $dm); $pen.Dispose(); $dm.Dispose()
  $g.DrawString([string][int]($s[1] * 100), $fTxt, $brTxt, 436, ($sy - 9))
  $sy += 46
}

# ============ 2. item cards ==============================================
$g.DrawString('Item cards', $fLbl, $brLbl, 540, 18)
for ($i = 0; $i -lt 3; $i++) {
  $cx = 540 + $i * 152
  $c = RoundPath $cx 40 138 190 4
  FillV $g $c $cx 40 138 190 (C 236 $RAISED) (C 246 $PANEL_BOT)
  $pen = New-Object System.Drawing.Pen((C 120 $LINE), 1.0); $g.DrawPath($pen, $c); $pen.Dispose(); $c.Dispose()
  # gold chevron accent centred on the top edge
  $ch = New-Object System.Drawing.Drawing2D.GraphicsPath
  $mid = $cx + 69
  $pts = New-Object 'System.Drawing.PointF[]' 3
  $pts[0] = New-Object System.Drawing.PointF(($mid - 11), 40)
  $pts[1] = New-Object System.Drawing.PointF($mid, 50)
  $pts[2] = New-Object System.Drawing.PointF(($mid + 11), 40)
  $ch.AddPolygon($pts)
  $br = New-Object System.Drawing.SolidBrush((C 220 $GOLD)); $g.FillPath($br, $ch); $br.Dispose(); $ch.Dispose()
  $hl = New-Object System.Drawing.Pen((C 90 $LINE_HI), 1.0)
  $g.DrawLine($hl, ($cx + 12), 74, ($cx + 126), 74); $hl.Dispose()
  $g.DrawString('Item ' + ($i + 1), $fTxt, $brTxt, ($cx + 14), 54)
  $gb = New-Object System.Drawing.SolidBrush((C 230 $GOLD_LT))
  $g.DrawString('5,000', $fTxt, $gb, ($cx + 14), 200); $gb.Dispose()
}

# ============ 3. CTA + secondary buttons =================================
$g.DrawString('Buttons', $fLbl, $brLbl, 540, 254)
$cta = RoundPath 540 276 290 44 3
FillV $g $cta 540 276 290 44 (C 255 $CTA_A) (C 255 $CTA_B)
$pen = New-Object System.Drawing.Pen((C 220 $GOLD_LT), 1.0); $g.DrawPath($pen, $cta); $pen.Dispose(); $cta.Dispose()
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$rr = New-Object System.Drawing.RectangleF(540, 276, 290, 44)
$bb = New-Object System.Drawing.SolidBrush((C 255 '2A1F06'))
$fB = New-Object System.Drawing.Font('Segoe UI Semibold', 11)
$g.DrawString('Apply', $fB, $bb, $rr, $sf); $bb.Dispose()
for ($i = 0; $i -lt 2; $i++) {
  $bx = 842 + $i * 76
  $b2 = RoundPath $bx 276 68 44 3
  FillV $g $b2 $bx 276 68 44 (C 220 $RAISED) (C 236 $PANEL_BOT)
  $pen = New-Object System.Drawing.Pen((C 130 $LINE), 1.0); $g.DrawPath($pen, $b2); $pen.Dispose(); $b2.Dispose()
}
$fB.Dispose()

# ============ 4. HP / MP =================================================
$g.DrawString('HP / MP', $fLbl, $brLbl, 34, 312)
$by = 336
foreach ($bar in @(@($HP, 0.74), @($MP, 0.52))) {
  $t = RoundPath 34 $by 300 12 2
  FillV $g $t 34 $by 300 12 (C 235 '0A0D14') (C 235 '141A26')
  $pen = New-Object System.Drawing.Pen((C 90 $LINE), 1.0); $g.DrawPath($pen, $t); $pen.Dispose(); $t.Dispose()
  $fw = [int](296 * $bar[1])
  $f = RoundPath 36 ($by + 2) $fw 8 2
  FillV $g $f 36 ($by + 2) $fw 8 (C 255 $bar[0]) (C 210 $bar[0])
  $br = New-Object System.Drawing.SolidBrush((C 60 'FFFFFF'))
  $g.FillRectangle($br, 36, ($by + 2), $fw, 3); $br.Dispose()
  $f.Dispose()
  $by += 20
}

# ============ 5. quickslots ==============================================
$g.DrawString('Quickslots', $fLbl, $brLbl, 34, 388)
for ($i = 0; $i -lt 8; $i++) {
  $qx = 34 + $i * 52
  $q = RoundPath $qx 410 46 46 3
  FillV $g $q $qx 410 46 46 (C 210 $RAISED) (C 235 '070A10')
  $pen = New-Object System.Drawing.Pen((C 120 $LINE), 1.0); $g.DrawPath($pen, $q); $pen.Dispose(); $q.Dispose()
  $kb = New-Object System.Drawing.SolidBrush((C 130 $TXT_DIM))
  $fk = New-Object System.Drawing.Font('Segoe UI', 7)
  $g.DrawString([string]($i + 1), $fk, $kb, ($qx + 3), 412); $kb.Dispose(); $fk.Dispose()
  if ($i -eq 2) {
    $sel = RoundPath ($qx - 1) 409 48 48 3
    Glow $g $sel $TEAL 3
    $pen = New-Object System.Drawing.Pen((C 230 $TEAL), 1.3); $g.DrawPath($pen, $sel); $pen.Dispose(); $sel.Dispose()
  }
}

# ============ 6. list rows ===============================================
$g.DrawString('List rows', $fLbl, $brLbl, 540, 340)
$lp = RoundPath 540 362 420 190 4
FillV $g $lp 540 362 420 190 (C 226 $PANEL_TOP) (C 242 $PANEL_BOT)
$pen = New-Object System.Drawing.Pen((C 110 $LINE), 1.0); $g.DrawPath($pen, $lp); $pen.Dispose(); $lp.Dispose()
for ($i = 0; $i -lt 3; $i++) {
  $ry = 376 + $i * 58
  if ($i -eq 0) {
    $selp = RoundPath 548 $ry 404 50 3
    FillV $g $selp 548 $ry 404 50 (C 90 $TEAL_DIM) (C 40 $TEAL_DIM)
    $pen = New-Object System.Drawing.Pen((C 120 $TEAL), 1.0); $g.DrawPath($pen, $selp); $pen.Dispose(); $selp.Dispose()
  }
  $av = RoundPath 558 ($ry + 6) 38 38 3
  FillV $g $av 558 ($ry + 6) 38 38 (C 235 $RAISED) (C 235 '070A10')
  $pen = New-Object System.Drawing.Pen((C 120 $LINE_HI), 1.0); $g.DrawPath($pen, $av); $pen.Dispose(); $av.Dispose()
  $g.DrawString('Player ' + ($i + 1), $fTxt, $brTxt, 608, ($ry + 8))
  $g.DrawString('Lv 45', $fTxt, $brDim, 608, ($ry + 26))
  if ($i -lt 2) {
    $sp = New-Object System.Drawing.Pen((C 55 $LINE), 1.0)
    $g.DrawLine($sp, 558, ($ry + 56), 942, ($ry + 56)); $sp.Dispose()
  }
}

# ============ 7. scrollbar ===============================================
$tr2 = RoundPath 968 40 8 250 3
FillV $g $tr2 968 40 8 250 (C 150 '070A10') (C 150 '121824')
$g.FillPath((New-Object System.Drawing.SolidBrush((C 120 '0A0E16'))), $tr2); $tr2.Dispose()
$th = RoundPath 968 60 8 90 3
FillV $g $th 968 60 8 90 (C 220 $LINE_HI) (C 220 $LINE)
$th.Dispose()

$fLbl.Dispose(); $fTxt.Dispose(); $fHead.Dispose(); $fTab.Dispose()
$brLbl.Dispose(); $brTxt.Dispose(); $brDim.Dispose()
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("wrote " + $Out)
