# Draws the application icon (src\SacredModManager\Assets\app.ico and app.png) with System.Drawing, so that it can be reproduced and tweaked.
#
# The idea: a steel cog (the "Gear" of Sacred Gear) shaped like a medallion, like the round emblem of Sacred.exe and Config.exe: dark metal rim,
# teal rune glow in the middle, and a small golden four-point rune star (the gold of the GOG icon). Nothing is drawn around the cog: the
# background is fully transparent. Small sizes are drawn with fewer details.
#
#   powershell -File tools\make_icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\src\SacredModManager\Assets'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function C([int]$a, [string]$hex) {
    $h = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
}

function New-GearPath([double]$cx, [double]$cy, [double]$rOuter, [double]$rRoot, [int]$teeth, [double]$toothFill) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    $step = 2 * [Math]::PI / $teeth
    for ($i = 0; $i -lt $teeth; $i++) {
        $a = $i * $step - [Math]::PI / 2
        $half = $step * $toothFill / 2          # half of the tooth's width at the root
        $tip = $half * 0.62                      # the tip is narrower than the root
        foreach ($p in @(@(($a - $half), $rRoot), @(($a - $tip), $rOuter), @(($a + $tip), $rOuter), @(($a + $half), $rRoot), @(($a + $step / 2), $rRoot))) {
            $pts.Add([System.Drawing.PointF]::new([float]($cx + [Math]::Cos($p[0]) * $p[1]), [float]($cy + [Math]::Sin($p[0]) * $p[1])))
        }
    }
    $path.AddPolygon($pts.ToArray())
    return $path
}

function Draw-Icon([int]$size) {
    $detail = $size -ge 48
    $ss = 4                                   # supersampling: draw big, shrink with a good filter
    $s = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $c = $s / 2.0
    $teeth = if ($size -ge 32) { 12 } else { 8 }

    # the cog: dark steel with a light edge at the top left
    $gear = New-GearPath $c $c ($s * 0.485) ($s * 0.415) $teeth 0.72
    $steel = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), (C 255 '7b8791'), (C 255 '1d2329')
    $g.FillPath($steel, $gear)
    $edge = New-Object System.Drawing.Pen (C 255 '0c1013'), ([float]($s * 0.012))
    $edge.LineJoin = 'Round'
    $g.DrawPath($edge, $gear)
    if ($detail) {
        $shine = New-Object System.Drawing.Pen (C 70 'ffffff'), ([float]($s * 0.008))
        $g.SetClip($gear)
        $g.DrawEllipse($shine, [float]($c - $s * 0.455), [float]($c - $s * 0.455), [float]($s * 0.91), [float]($s * 0.91))
        $g.ResetClip()
        $shine.Dispose()
    }

    # inner bevel ring (gold rim like the GOG icon, thin)
    $rRing = $s * 0.345
    $ring = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), (C 255 'f3cf74'), (C 255 '8a5f12')
    $g.FillEllipse($ring, [float]($c - $rRing), [float]($c - $rRing), [float](2 * $rRing), [float](2 * $rRing))
    $rDark = $s * 0.318
    $dark = New-Object System.Drawing.SolidBrush (C 255 '10161a')
    $g.FillEllipse($dark, [float]($c - $rDark), [float]($c - $rDark), [float](2 * $rDark), [float](2 * $rDark))

    # the teal rune glow
    $rGlow = $s * 0.285
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse([float]($c - $rGlow), [float]($c - $rGlow), [float](2 * $rGlow), [float](2 * $rGlow))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glow.CenterPoint = [System.Drawing.PointF]::new([float]($c - $s * 0.03), [float]($c - $s * 0.04))
    $glow.CenterColor = C 255 'd8fff7'
    $glow.SurroundColors = @((C 255 '0e5a66'))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 4
    $blend.Colors = @((C 255 '0b4652'), (C 255 '1fa7a6'), (C 255 '7cf0e0'), (C 255 'e6fffb'))
    $blend.Positions = @(0.0, 0.45, 0.8, 1.0)
    $glow.InterpolationColors = $blend
    $g.FillEllipse($glow, [float]($c - $rGlow), [float]($c - $rGlow), [float](2 * $rGlow), [float](2 * $rGlow))

    # four-point rune star in gold, dark outline
    $rStar = $s * 0.235
    $rWaist = $s * 0.062
    $star = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sp = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($i = 0; $i -lt 8; $i++) {
        $a = $i * [Math]::PI / 4 - [Math]::PI / 2
        $r = if ($i % 2 -eq 0) { $rStar } else { $rWaist }
        $sp.Add([System.Drawing.PointF]::new([float]($c + [Math]::Cos($a) * $r), [float]($c + [Math]::Sin($a) * $r)))
    }
    $star.AddPolygon($sp.ToArray())
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new([float]($c - $rStar), [float]($c - $rStar))), ([System.Drawing.PointF]::new([float]($c + $rStar), [float]($c + $rStar))), (C 255 'fff0b0'), (C 255 'b9740e')
    $starPen = New-Object System.Drawing.Pen (C 230 '2a1a04'), ([float]($s * 0.012))
    $starPen.LineJoin = 'Round'
    $g.FillPath($gold, $star)
    $g.DrawPath($starPen, $star)

    # the hub: a small teal jewel in the middle of the star
    $rHub = $s * 0.05
    $hub = New-Object System.Drawing.SolidBrush (C 255 '0f6f78')
    $g.FillEllipse($hub, [float]($c - $rHub), [float]($c - $rHub), [float](2 * $rHub), [float](2 * $rHub))
    $hubLight = New-Object System.Drawing.SolidBrush (C 255 'c8fff6')
    $rHl = $s * 0.022
    $g.FillEllipse($hubLight, [float]($c - $rHl - $s * 0.01), [float]($c - $rHl - $s * 0.012), [float](2 * $rHl), [float](2 * $rHl))

    # rivets on the cog between the teeth ring and the gold ring
    if ($detail) {
        $rb = New-Object System.Drawing.SolidBrush (C 255 'aeb8c0')
        $rbDark = New-Object System.Drawing.Pen (C 255 '0c1013'), ([float]($s * 0.006))
        for ($i = 0; $i -lt 6; $i++) {
            $a = $i * [Math]::PI / 3 + [Math]::PI / 6
            $px = $c + [Math]::Cos($a) * $s * 0.385
            $py = $c + [Math]::Sin($a) * $s * 0.385
            $rr = $s * 0.014
            $g.FillEllipse($rb, [float]($px - $rr), [float]($py - $rr), [float](2 * $rr), [float](2 * $rr))
            $g.DrawEllipse($rbDark, [float]($px - $rr), [float]($py - $rr), [float](2 * $rr), [float](2 * $rr))
        }
    }

    $g.Dispose()
    $out = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.PixelOffsetMode = 'HighQuality'
    $g2.SmoothingMode = 'AntiAlias'
    $g2.DrawImage($bmp, 0, 0, $size, $size)
    $g2.Dispose()
    $bmp.Dispose()
    return $out
}

function To-Png([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$sizes = 256, 128, 64, 48, 32, 24, 16
$frames = @{}
foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $frames[$sz] = To-Png $bmp
    if ($sz -eq 256) { $bmp.Save((Join-Path $Out 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $data = $frames[$sz]
    $bw.Write([byte]($sz % 256)); $bw.Write([byte]($sz % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($sz in $sizes) { $bw.Write($frames[$sz]) }
[IO.File]::WriteAllBytes((Join-Path $Out 'app.ico'), $ms.ToArray())
Write-Host "wrote app.ico ($($ms.Length) bytes) and app.png in $Out"
