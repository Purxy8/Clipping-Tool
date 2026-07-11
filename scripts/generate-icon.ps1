[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "..\assets"
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-ClipForgeBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $margin = [float]($Size * 0.035)
        $bounds = [System.Drawing.RectangleF]::new($margin, $margin, $Size - (2 * $margin), $Size - (2 * $margin))
        $backgroundPath = New-RoundedRectanglePath $bounds.X $bounds.Y $bounds.Width $bounds.Height ([float]($Size * 0.22))
        $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $bounds,
            [System.Drawing.Color]::FromArgb(255, 140, 123, 255),
            [System.Drawing.Color]::FromArgb(255, 82, 67, 195),
            45
        )
        try {
            $graphics.FillPath($backgroundBrush, $backgroundPath)
        }
        finally {
            $backgroundBrush.Dispose()
            $backgroundPath.Dispose()
        }

        $scale = [float]($Size / 24.0)
        $offset = [float](2 * $scale)
        $logoPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $logoPath.StartFigure()
            $logoPath.AddLine($offset + 4*$scale, $offset + 2*$scale, $offset + 16*$scale, $offset + 2*$scale)
            $logoPath.AddBezier(
                $offset + 16*$scale, $offset + 2*$scale,
                $offset + 17.1*$scale, $offset + 2*$scale,
                $offset + 18*$scale, $offset + 2.9*$scale,
                $offset + 18*$scale, $offset + 4*$scale)
            $logoPath.AddLine($offset + 18*$scale, $offset + 4*$scale, $offset + 18*$scale, $offset + 9*$scale)
            $logoPath.AddLine($offset + 18*$scale, $offset + 9*$scale, $offset + 15*$scale, $offset + 9*$scale)
            $logoPath.AddLine($offset + 15*$scale, $offset + 9*$scale, $offset + 15*$scale, $offset + 5*$scale)
            $logoPath.AddLine($offset + 15*$scale, $offset + 5*$scale, $offset + 5*$scale, $offset + 5*$scale)
            $logoPath.AddLine($offset + 5*$scale, $offset + 5*$scale, $offset + 5*$scale, $offset + 15*$scale)
            $logoPath.AddLine($offset + 5*$scale, $offset + 15*$scale, $offset + 10*$scale, $offset + 15*$scale)
            $logoPath.AddLine($offset + 10*$scale, $offset + 15*$scale, $offset + 10*$scale, $offset + 18*$scale)
            $logoPath.AddLine($offset + 10*$scale, $offset + 18*$scale, $offset + 4*$scale, $offset + 18*$scale)
            $logoPath.AddBezier(
                $offset + 4*$scale, $offset + 18*$scale,
                $offset + 2.9*$scale, $offset + 18*$scale,
                $offset + 2*$scale, $offset + 17.1*$scale,
                $offset + 2*$scale, $offset + 16*$scale)
            $logoPath.AddLine($offset + 2*$scale, $offset + 16*$scale, $offset + 2*$scale, $offset + 4*$scale)
            $logoPath.AddBezier(
                $offset + 2*$scale, $offset + 4*$scale,
                $offset + 2*$scale, $offset + 2.9*$scale,
                $offset + 2.9*$scale, $offset + 2*$scale,
                $offset + 4*$scale, $offset + 2*$scale)
            $logoPath.CloseFigure()

            $logoPath.StartFigure()
            $logoPath.AddPolygon([System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($offset + 12*$scale, $offset + 9*$scale),
                [System.Drawing.PointF]::new($offset + 19*$scale, $offset + 13.5*$scale),
                [System.Drawing.PointF]::new($offset + 12*$scale, $offset + 18*$scale)
            ))
            $graphics.FillPath([System.Drawing.Brushes]::White, $logoPath)
        }
        finally {
            $logoPath.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$iconImages = @()
foreach ($size in $iconSizes) {
    $bitmap = New-ClipForgeBitmap $size
    try {
        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $iconImages += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        if ($size -eq 256) {
            $bitmap.Save((Join-Path $OutputDirectory "ClipForge.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $stream.Dispose()
    }
    finally {
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $OutputDirectory "ClipForge.ico"
$fileStream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$iconImages.Count)
    $offset = 6 + (16 * $iconImages.Count)
    foreach ($image in $iconImages) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $iconImages) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

$splash = [System.Drawing.Bitmap]::new(640, 320, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$splashGraphics = [System.Drawing.Graphics]::FromImage($splash)
try {
    $splashGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $splashBounds = [System.Drawing.Rectangle]::new(0, 0, 640, 320)
    $splashBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $splashBounds,
        [System.Drawing.Color]::FromArgb(255, 11, 13, 18),
        [System.Drawing.Color]::FromArgb(255, 30, 24, 55),
        20
    )
    $splashGraphics.FillRectangle($splashBrush, $splashBounds)
    $splashBrush.Dispose()

    $logo = New-ClipForgeBitmap 132
    $splashGraphics.DrawImage($logo, 56, 94, 132, 132)
    $logo.Dispose()

    $titleFont = [System.Drawing.Font]::new("Segoe UI", 34, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subtitleFont = [System.Drawing.Font]::new("Segoe UI", 18, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $splashGraphics.DrawString("ClipForge", $titleFont, [System.Drawing.Brushes]::White, 220, 114)
        $subtitleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 170, 179, 197))
        $splashGraphics.DrawString("Capture the moment after it happens", $subtitleFont, $subtitleBrush, 224, 168)
        $subtitleBrush.Dispose()
    }
    finally {
        $titleFont.Dispose()
        $subtitleFont.Dispose()
    }

    $splash.Save((Join-Path $OutputDirectory "InstallSplash.png"), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $splashGraphics.Dispose()
    $splash.Dispose()
}

Write-Host "Generated ClipForge release artwork in: $OutputDirectory"
