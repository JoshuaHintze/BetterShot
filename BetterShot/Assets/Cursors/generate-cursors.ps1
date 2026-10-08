param(
    [string]$OutputDirectory = $PSScriptRoot,
    [string]$PreviewPath = (Join-Path $PSScriptRoot '..\..\..\artifacts\cursor-preview.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Add-Curve($Path, [float]$X1, [float]$Y1, [float]$ControlX, [float]$ControlY, [float]$X2, [float]$Y2) {
    $Path.AddBezier($X1, $Y1, $ControlX, $ControlY, $ControlX, $ControlY, $X2, $Y2)
}

function New-HandBitmap([int]$Size, [bool]$Closed) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $Size / 32.0
    $graphics.ScaleTransform($scale, $scale)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    if ($Closed) {
        # A compact closed fist: curled fingers on top, thumb crossing the palm.
        $path.AddBezier(8,13, 8,10, 10,9, 12,10)
        $path.AddBezier(12,10, 13,8, 16,8, 17,10)
        $path.AddBezier(17,10, 19,8, 22,9, 22,12)
        $path.AddBezier(22,12, 25,12, 26,14, 25,17)
        $path.AddLine(25,17, 24,23)
        Add-Curve $path 24 23 23 27 19 29
        Add-Curve $path 19 29 14 28 10 28
        Add-Curve $path 10 28 7 25 7 21
        $path.AddLine(7,21, 7,16)
        Add-Curve $path 7 16 7 13 8 13
        $path.CloseFigure()
    } else {
        # An open grab hand with four upright fingers and an angled thumb.
        $path.AddLine(8,16, 8,9)
        Add-Curve $path 8 9 9 6 10.5 6
        Add-Curve $path 10.5 6 13 7 13 9
        $path.AddLine(13,9, 13,5)
        Add-Curve $path 13 5 14 2 15.5 2
        Add-Curve $path 15.5 2 18 3 18 5
        $path.AddLine(18,5, 18,8)
        $path.AddLine(18,8, 18,5)
        Add-Curve $path 18 5 19 3 20.5 3
        Add-Curve $path 20.5 3 23 4 23 6
        $path.AddLine(23,6, 23,9)
        $path.AddLine(23,9, 23,7)
        Add-Curve $path 23 7 24 5 25.5 5
        Add-Curve $path 25.5 5 28 6 28 8
        $path.AddLine(28,8, 28,17)
        Add-Curve $path 28 17 28 24 24 29
        Add-Curve $path 24 29 18 29 13 29
        Add-Curve $path 13 29 10 26 8 22
        $path.AddLine(8,22, 4,17)
        Add-Curve $path 4 17 3 15 6 13
        Add-Curve $path 6 13 8 14 8 16
        $path.CloseFigure()
    }

    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 20, 20, 20), 1.65)
    $outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $graphics.FillPath($fill, $path)
    $graphics.DrawPath($outline, $path)

    if ($Closed) {
        foreach ($line in @(@(10,14,22,14), @(11,18,22,18), @(12,22,21,22), @(9,16,14,20))) {
            $graphics.DrawLine($outline, $line[0], $line[1], $line[2], $line[3])
        }
    } else {
        foreach ($line in @(@(13,8,13,17), @(18,8,18,17), @(23,9,23,17), @(8,16,11,21))) {
            $graphics.DrawLine($outline, $line[0], $line[1], $line[2], $line[3])
        }
    }

    $fill.Dispose(); $outline.Dispose(); $path.Dispose(); $graphics.Dispose()
    return $bitmap
}

function Get-DibBytes([System.Drawing.Bitmap]$Bitmap) {
    $width = $Bitmap.Width; $height = $Bitmap.Height
    $maskStride = [int][Math]::Ceiling($width / 32.0) * 4
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint32]40); $writer.Write([int32]$width); $writer.Write([int32]($height * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
    $writer.Write([uint32]($width * $height * 4)); $writer.Write([int32]0); $writer.Write([int32]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    for ($y = $height - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $width; $x++) {
            $color = $Bitmap.GetPixel($x, $y)
            $writer.Write([byte]$color.B); $writer.Write([byte]$color.G); $writer.Write([byte]$color.R); $writer.Write([byte]$color.A)
        }
    }
    for ($y = $height - 1; $y -ge 0; $y--) {
        $mask = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                $maskIndex = [int][Math]::Floor($x / 8.0)
                $mask[$maskIndex] = $mask[$maskIndex] -bor (0x80 -shr ($x % 8))
            }
        }
        $writer.Write($mask)
    }
    $writer.Flush(); $bytes = $stream.ToArray(); $writer.Dispose(); $stream.Dispose()
    return $bytes
}

function Write-Cursor([string]$Path, [bool]$Closed) {
    $sizes = @(32, 48, 64)
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = New-HandBitmap $size $Closed
        try { $images.Add([byte[]](Get-DibBytes $bitmap)) } finally { $bitmap.Dispose() }
    }
    $stream = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0); $writer.Write([uint16]2); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]; $hotX = [int]($size * 0.5); $hotY = [int]($size * 0.5)
        $writer.Write([byte]$size); $writer.Write([byte]$size); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]$hotX); $writer.Write([uint16]$hotY); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Dispose(); $stream.Dispose()
}

function Write-Preview([string]$Path) {
    $preview = [System.Drawing.Bitmap]::new(960, 540, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($preview)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 28, 29, 33))
    $font = [System.Drawing.Font]::new('Segoe UI', 24, [System.Drawing.FontStyle]::Bold)
    $small = [System.Drawing.Font]::new('Segoe UI', 14)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $muted = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 188, 190, 198))
    $graphics.DrawString('BetterShot canvas navigation cursors', $font, $white, 42, 30)
    $graphics.DrawString('Actual 64 px cursor frames enlarged 5x', $small, $muted, 45, 75)
    $cards = @(@('OPEN GRAB', $false, 55), @('CLOSED GRABBING', $true, 500))
    foreach ($card in $cards) {
        $x = $card[2]
        $graphics.FillRectangle([System.Drawing.Brushes]::White, $x, 120, 405, 350)
        $graphics.FillRectangle([System.Drawing.Brushes]::Black, $x + 203, 120, 202, 350)
        $cursor = New-HandBitmap 64 ([bool]$card[1])
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $graphics.DrawImage($cursor, $x + 42, 140, 320, 320)
        $cursor.Dispose()
        $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 196, 35))
        $graphics.DrawString([string]$card[0], $small, $labelBrush, $x + 12, 480)
        $labelBrush.Dispose()
    }
    $directory = Split-Path -Parent $Path
    if ($directory) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    $preview.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $font.Dispose(); $small.Dispose(); $white.Dispose(); $muted.Dispose(); $graphics.Dispose(); $preview.Dispose()
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
Write-Cursor (Join-Path $OutputDirectory 'Grab.cur') $false
Write-Cursor (Join-Path $OutputDirectory 'Grabbing.cur') $true
Write-Preview $PreviewPath
