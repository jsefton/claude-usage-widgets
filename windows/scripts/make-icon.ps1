<#
.SYNOPSIS
  Draws the app icon (a budget gauge on a warm rounded square) and writes a multi-size .ico.

.DESCRIPTION
  The same design as mac/scripts/make-icon.swift (the macOS icon), scaled to fill more of the canvas
  as Windows icons do. Writes src/ClaudeUsageTray/AppIcon.ico with 16-256 px images.
  Run it again after changing the design; the .ico is committed.

.EXAMPLE
  windows/scripts/make-icon.ps1
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\ClaudeUsageTray\AppIcon.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-Color([int]$hex, [int]$alpha = 255) {
    [System.Drawing.Color]::FromArgb($alpha, ($hex -shr 16) -band 0xFF, ($hex -shr 8) -band 0xFF, $hex -band 0xFF)
}

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

# Draws the icon in a 1024x1024 space (origin top-left), scaled to $pixels.
function New-IconBitmap([int]$pixels) {
    $bmp = New-Object System.Drawing.Bitmap $pixels, $pixels, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $pixels / 1024.0
    $g.ScaleTransform($s, $s)

    # Body: warm vertical gradient on a rounded square (the macOS body is 824/1024; Windows icons fill more).
    $k = 960 / 824.0
    $body = New-RoundedRect 32 32 960 960 (185 * $k)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 512, 32), (New-Object System.Drawing.PointF 512, 992), (Get-Color 0xF0956A), (Get-Color 0xB94E2A)
    $g.FillPath($gradient, $body)

    # Gauge: a 240-degree arc from bottom-left, over the top, to bottom-right (GDI+ angles run clockwise).
    $cx = 512.0; $cy = 512 + 42 * $k
    $radius = 255 * $k
    $fraction = 0.62
    $rect = New-Object System.Drawing.RectangleF ($cx - $radius), ($cy - $radius), (2 * $radius), (2 * $radius)

    $track = New-Object System.Drawing.Pen (Get-Color 0xFFFFFF 71), (72 * $k)
    $track.StartCap = $track.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($track, $rect, 150, 240)

    $fill = New-Object System.Drawing.Pen (Get-Color 0xFFFFFF), (72 * $k)
    $fill.StartCap = $fill.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($fill, $rect, 150, 240 * $fraction)

    # Needle pointing at the current level.
    $level = (150 + 240 * $fraction) * [Math]::PI / 180
    $needle = New-Object System.Drawing.Pen (Get-Color 0xFFFFFF), (38 * $k)
    $needle.StartCap = $needle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($needle, [float]$cx, [float]$cy, [float]($cx + [Math]::Cos($level) * 175 * $k), [float]($cy + [Math]::Sin($level) * 175 * $k))

    # Hub.
    $hub = 52 * $k; $dot = 20 * $k
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Get-Color 0xFFFFFF)), [float]($cx - $hub), [float]($cy - $hub), [float](2 * $hub), [float](2 * $hub))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Get-Color 0xC8603A)), [float]($cx - $dot), [float]($cy - $dot), [float](2 * $dot), [float](2 * $dot))

    $g.Dispose()
    $bmp
}

# 32-bit DIB image for an .ico entry: BITMAPINFOHEADER (double height), bottom-up BGRA rows, empty AND mask.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $w * 4)
        $bw.Write($row)
    }
    $bmp.UnlockBits($data)
    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $h)))
    $bw.Flush()
    , $ms.ToArray()
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    # Small sizes as DIBs (loadable everywhere); 256 px as PNG, as Windows expects.
    if ($size -ge 256) { $bytes = Get-PngBytes $bmp } else { $bytes = Get-DibBytes $bmp }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dim = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$image.Bytes.Length); $bw.Write([int]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $bw.Write($image.Bytes) }
$bw.Flush()

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
Write-Host "Wrote $OutFile"
