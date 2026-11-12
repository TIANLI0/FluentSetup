<#
.SYNOPSIS
  Regenerates assets/default-app.ico: the neutral icon used by Setup.exe when a project has no icon of its own.
  Drawn procedurally (rounded tile + package glyph) so the repository contains no third-party artwork.
#>
param([string] $Out = (Join-Path $PSScriptRoot '..\assets\default-app.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Frame([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0
    # Tile: rounded square with a subtle vertical gradient (Windows 11 blue).
    $r = [System.Drawing.RectangleF]::new(4 * $s, 4 * $s, 56 * $s, 56 * $s)
    $radius = 12 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 0, 84, 166)), 90
    $g.FillPath($brush, $path)
    # Package glyph: open box with a down arrow.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 3.2 * $s))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(18 * $s, 30 * $s), [System.Drawing.PointF]::new(18 * $s, 46 * $s),
        [System.Drawing.PointF]::new(46 * $s, 46 * $s), [System.Drawing.PointF]::new(46 * $s, 30 * $s)))
    $g.DrawLine($pen, 32 * $s, 14 * $s, 32 * $s, 36 * $s)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(24 * $s, 28 * $s), [System.Drawing.PointF]::new(32 * $s, 36 * $s), [System.Drawing.PointF]::new(40 * $s, 28 * $s)))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) { , (New-Frame $s) }
$icoStream = New-Object System.IO.MemoryStream
$w = [System.IO.BinaryWriter]::new($icoStream)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllBytes($Out, $icoStream.ToArray())
"Wrote $Out ($($icoStream.Length) bytes)"
