<#
.SYNOPSIS
    Draws Issun's placeholder icon and writes src/Issun/Assets/issun.ico.

.DESCRIPTION
    A PLACEHOLDER. Replace it whenever a real icon exists: overwrite
    src/Issun/Assets/issun.ico with any .ico that carries 16-256 px frames and
    rebuild. Nothing else refers to this script.

    The idea, so a replacement can keep it or knowingly drop it: in Okami, Issun
    is the tiny glowing Poncle who rides along with Amaterasu - "Ammy" - and does
    the talking she can't. That is this app's job for the phone. Ammy's own icon
    is sumi-e ink on paper with red accents, so this is drawn in the same spirit:
    warm paper, one black brush ring (an enso), and a small green glow at its
    centre where Issun sits. A red seal is added only where there is room for it.

    Every size is drawn directly rather than scaled down from 256, because a ring
    shrunk to 16 px turns to grey mush; the small sizes get a heavier stroke.

    Rerunnable, and deterministic: the brush texture comes from a fixed seed, so
    running it twice produces the same bytes.

    Works in Windows PowerShell 5.1 and PowerShell 7 (both carry System.Drawing).

.PARAMETER Preview
    Also write a contact sheet of every size, magnified, to this PNG path.

.EXAMPLE
    pwsh -File tools/make-icon.ps1
    pwsh -File tools/make-icon.ps1 -Preview $env:TEMP\issun-icon-preview.png
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\Issun\Assets\issun.ico'),
    [string]$Preview = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# Colours. Ink is a warm black rather than #000 - sumi ink on paper reads brown
# at its thin edges. The glow is a yellow-green, Issun's colour in the game.
$Paper     = [System.Drawing.Color]::FromArgb(255, 243, 233, 212)
$PaperEdge = [System.Drawing.Color]::FromArgb(255, 205, 186, 150)
$Ink       = [System.Drawing.Color]::FromArgb(255, 26, 22, 20)
$Seal      = [System.Drawing.Color]::FromArgb(255, 190, 38, 44)
$GlowCore  = [System.Drawing.Color]::FromArgb(255, 250, 255, 226)
$GlowMid   = [System.Drawing.Color]::FromArgb(255, 150, 226, 92)
$GlowSmall = [System.Drawing.Color]::FromArgb(255, 186, 250, 120)

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# One brush stroke around the ring, as a filled outline: the pen loads heavily at
# the start and runs dry towards the end. A GDI+ pen cannot vary its width
# along a path, so the outline is built point by point.
function Add-BrushArc($g, [single]$cx, [single]$cy, [single]$radius,
                      [double]$startDeg, [double]$sweepDeg,
                      [single]$wStart, [single]$wEnd, $color) {
    $steps = 120
    $outer = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $inner = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -le $steps; $i++) {
        $t = $i / $steps
        $a = ($startDeg + $sweepDeg * $t) * [Math]::PI / 180
        # Thick for most of the way, then a quick taper - how a loaded brush
        # behaves - plus a slight wobble so the circle looks drawn, not computed.
        $w = $wEnd + ($wStart - $wEnd) * [Math]::Pow(1 - $t, 0.55)
        $r = $radius * (1 + 0.018 * [Math]::Sin($t * 3 * [Math]::PI))
        $outer.Add((New-Object System.Drawing.PointF(($cx + [Math]::Cos($a) * ($r + $w / 2)), ($cy + [Math]::Sin($a) * ($r + $w / 2)))))
        $inner.Add((New-Object System.Drawing.PointF(($cx + [Math]::Cos($a) * ($r - $w / 2)), ($cy + [Math]::Sin($a) * ($r - $w / 2)))))
    }
    $inner.Reverse()
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $pts.AddRange($outer); $pts.AddRange($inner)
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillPolygon($brush, $pts.ToArray())
    # The rounded blob where the brush first touched the paper.
    $a0 = $startDeg * [Math]::PI / 180
    $sx = $cx + [Math]::Cos($a0) * $radius
    $sy = $cy + [Math]::Sin($a0) * $radius
    $g.FillEllipse($brush, [single]($sx - $wStart / 2), [single]($sy - $wStart / 2), $wStart, $wStart)
    $brush.Dispose()
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $small = $size -le 24

    # Paper ground. Nearly edge to edge at small sizes, where every pixel counts.
    $inset = if ($small) { 0.0 } else { $s * 0.04 }
    $paperPath = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.2)
    $paperBrush = New-Object System.Drawing.SolidBrush($Paper)
    $g.FillPath($paperBrush, $paperPath)
    if ($size -ge 32) {
        $edgePen = New-Object System.Drawing.Pen($PaperEdge, [single][Math]::Max(1, $s / 128))
        $g.DrawPath($edgePen, $paperPath)
        $edgePen.Dispose()
    }

    # Paper grain, only where it can be seen.
    if ($size -ge 64) {
        $rng = New-Object System.Random(20260921)
        $g.SetClip($paperPath)
        for ($i = 0; $i -lt $size * 6; $i++) {
            $alpha = $rng.Next(6, 18)
            $grain = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alpha, 120, 90, 50))
            $gx = [single]($rng.NextDouble() * $s); $gy = [single]($rng.NextDouble() * $s)
            $gs = [single]($s / 256 * (1 + $rng.NextDouble() * 1.5))
            $g.FillEllipse($grain, $gx, $gy, $gs, $gs)
            $grain.Dispose()
        }
        $g.ResetClip()
    }

    $cx = $s / 2; $cy = $s / 2

    # Issun's glow, drawn before the ring so the ink sits on top of its halo.
    $glowR = if ($small) { $s * 0.24 } else { $s * 0.2 }
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse([single]($cx - $glowR), [single]($cy - $glowR), [single](2 * $glowR), [single](2 * $glowR))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = $GlowMid
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $GlowMid.R, $GlowMid.G, $GlowMid.B))
    $g.FillPath($glow, $glowPath)
    $glow.Dispose(); $glowPath.Dispose()

    # At the small sizes a near-white core disappears into the paper, so the
    # core itself carries the green there.
    $coreR = [Math]::Max([double]$s * 0.07, 1.25)
    $coreColor = if ($small) { $GlowSmall } else { $GlowCore }
    $coreBrush = New-Object System.Drawing.SolidBrush($coreColor)
    $g.FillEllipse($coreBrush, [single]($cx - $coreR), [single]($cy - $coreR), [single](2 * $coreR), [single](2 * $coreR))
    $coreBrush.Dispose()

    # The enso. Heavier at small sizes so it survives as a ring at 16 px. The
    # brush lands at the bottom, travels clockwise and lifts off at the lower
    # right, leaving the ring open there. An opening at the top reads as a power
    # button - the first draft did exactly that.
    $ringR = if ($small) { $s * 0.335 } else { $s * 0.315 }
    $wStart = if ($small) { $s * 0.2 } else { $s * 0.14 }
    $wEnd = if ($small) { $s * 0.09 } else { $s * 0.03 }
    Add-BrushArc $g $cx $cy $ringR 100 305 $wStart $wEnd $Ink

    # Dry-brush streaks off the tail: the bristles separating as the ink runs
    # out. Only where they are more than a smudge.
    if ($size -ge 48) {
        $streak = [System.Drawing.Color]::FromArgb(150, $Ink.R, $Ink.G, $Ink.B)
        foreach ($offset in @(-0.085, 0.075)) {
            Add-BrushArc $g $cx $cy ([single]($ringR * (1 + $offset))) 350 62 ([single]($s * 0.014)) ([single]($s * 0.003)) $streak
        }
    }

    # A red seal, the way a sumi-e painting is signed, and Ammy's red accent. It
    # sits in the opening of the ring.
    if ($size -ge 40) {
        $sealSize = $s * 0.14
        $sx = $s * 0.8 - $sealSize / 2; $sy = $s * 0.8 - $sealSize / 2
        $sealPath = New-RoundedRect ([single]$sx) ([single]$sy) ([single]$sealSize) ([single]$sealSize) ([single]($sealSize * 0.14))
        $sealBrush = New-Object System.Drawing.SolidBrush($Seal)
        $g.FillPath($sealBrush, $sealPath)
        $sealBrush.Dispose(); $sealPath.Dispose()
        if ($size -ge 128) {
            # The carved border of a stamp. Not a bar across the middle: that
            # read as a "remove" badge.
            $inner = $sealSize * 0.16
            $framePath = New-RoundedRect ([single]($sx + $inner)) ([single]($sy + $inner)) ([single]($sealSize - 2 * $inner)) ([single]($sealSize - 2 * $inner)) ([single]($sealSize * 0.06))
            $framePen = New-Object System.Drawing.Pen($Paper, [single]($sealSize * 0.07))
            $g.DrawPath($framePen, $framePath)
            $framePen.Dispose(); $framePath.Dispose()
        }
    }

    $paperBrush.Dispose(); $paperPath.Dispose()
    $g.Dispose()
    return $bmp
}

# 32-bit BMP frame (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask). Used
# for the small sizes, which every consumer of .ico files understands.
function Get-BmpFrame($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $h)))
    $bw.Flush()
    return $ms.ToArray()
}

# PNG frame, for 128 and 256: a 256 px BMP would be 256 KB of mostly paper.
function Get-PngFrame($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$frames = @()
$bitmaps = @{}
foreach ($size in $Sizes) {
    $bmp = Draw-Icon $size
    $bitmaps[$size] = $bmp
    $data = if ($size -ge 128) { Get-PngFrame $bmp } else { Get-BmpFrame $bmp }
    $frames += , @($size, $data)
}

$out = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($out)) | Out-Null
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $size = $f[0]; $data = $f[1]
    $dim = if ($size -ge 256) { 0 } else { $size }     # 0 means 256 in an ICONDIRENTRY
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Flush(); $fs.Dispose()
Write-Host "wrote $out ($((Get-Item $out).Length) bytes, $($frames.Count) sizes)"

if ($Preview) {
    # Every size at 1:1 on a light and a dark strip, plus the small ones
    # magnified so individual pixels can be judged.
    $zoom = 6
    $sheetW = [int](40 + ($Sizes | Measure-Object -Sum).Sum + 20 * $Sizes.Count)
    $sheetH = [int](2 * (256 + 40) + 48 * $zoom + 60)
    $sheet = New-Object System.Drawing.Bitmap($sheetW, $sheetH)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 243, 243, 243))
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
    $g.FillRectangle($dark, 0, 296, $sheetW, 296)
    $x = 20
    foreach ($size in $Sizes) {
        $g.DrawImageUnscaled($bitmaps[$size], $x, 20)
        $g.DrawImageUnscaled($bitmaps[$size], $x, 316)
        $x += $size + 20
    }
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $x = 20
    foreach ($size in @(16, 20, 24, 32, 48)) {
        $g.DrawImage($bitmaps[$size], $x, 612, $size * $zoom, $size * $zoom)
        $x += $size * $zoom + 20
    }
    $g.Dispose()
    $sheet.Save([System.IO.Path]::GetFullPath($Preview), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "preview $Preview"
}
