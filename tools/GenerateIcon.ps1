param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\assets\TinyHwBar.ico'),
    [string]$MasterPngPath = (Join-Path $PSScriptRoot '..\assets\TinyHwBar.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function Convert-BitmapToIconFrame {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap
    )

    $size = $Bitmap.Width
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $pixelBytes = $size * $size * 4
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    try {
        $writer.Write([UInt32]40)
        $writer.Write([Int32]$size)
        $writer.Write([Int32]($size * 2))
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]0)
        $writer.Write([UInt32]$pixelBytes)
        $writer.Write([Int32]0)
        $writer.Write([Int32]0)
        $writer.Write([UInt32]0)
        $writer.Write([UInt32]0)

        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $color = $Bitmap.GetPixel($x, $y)
                $writer.Write([Byte]$color.B)
                $writer.Write([Byte]$color.G)
                $writer.Write([Byte]$color.R)
                $writer.Write([Byte]$color.A)
            }
        }

        for ($y = $size - 1; $y -ge 0; $y--) {
            [Byte[]]$maskRow = New-Object Byte[] $maskStride
            for ($x = 0; $x -lt $size; $x++) {
                if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                    $byteIndex = [int][Math]::Floor($x / 8.0)
                    $bitIndex = 7 - ($x % 8)
                    $maskRow[$byteIndex] = $maskRow[$byteIndex] -bor (1 -shl $bitIndex)
                }
            }

            $writer.Write($maskRow)
        }

        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Convert-BitmapToPngFrame {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Bitmap]$Bitmap
    )

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function New-GaugeBitmap {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $accent = [System.Drawing.Color]::FromArgb(255, 120, 119, 247)
        $strokeWidth = [Math]::Max(1.15, $Size * 0.038)
        $detailStrokeWidth = [Math]::Max(1.0, $strokeWidth * 0.88)
        $arcPen = New-Object System.Drawing.Pen($accent, $strokeWidth)
        $detailPen = New-Object System.Drawing.Pen($accent, $detailStrokeWidth)

        try {
            foreach ($pen in @($arcPen, $detailPen)) {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            }

            $arcBounds = New-Object System.Drawing.RectangleF(
                [Single]($Size * 0.15),
                [Single]($Size * 0.18),
                [Single]($Size * 0.70),
                [Single]($Size * 0.78))
            $graphics.DrawArc($arcPen, $arcBounds, [Single]180.0, [Single]180.0)

            $graphics.DrawLine(
                $detailPen,
                [Single]($Size * 0.15),
                [Single]($Size * 0.57),
                [Single]($Size * 0.15),
                [Single]($Size * 0.64))
            $graphics.DrawLine(
                $detailPen,
                [Single]($Size * 0.85),
                [Single]($Size * 0.57),
                [Single]($Size * 0.85),
                [Single]($Size * 0.64))
            $graphics.DrawLine(
                $detailPen,
                [Single]($Size * 0.50),
                [Single]($Size * 0.18),
                [Single]($Size * 0.50),
                [Single]($Size * 0.26))

            $hubX = [Single]($Size * 0.50)
            $hubY = [Single]($Size * 0.68)
            $graphics.DrawLine(
                $arcPen,
                $hubX,
                $hubY,
                [Single]($Size * 0.70),
                [Single]($Size * 0.40))

            $hubRadius = [Math]::Max(1.25, $Size * 0.052)
            $innerRadius = $hubRadius
            $transparentBrush = New-Object System.Drawing.SolidBrush(
                [System.Drawing.Color]::Transparent)
            try {
                $graphics.CompositingMode =
                    [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.FillEllipse(
                    $transparentBrush,
                    [Single]($hubX - $innerRadius),
                    [Single]($hubY - $innerRadius),
                    [Single]($innerRadius * 2.0),
                    [Single]($innerRadius * 2.0))
            }
            finally {
                $transparentBrush.Dispose()
                $graphics.CompositingMode =
                    [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            }

            $graphics.DrawEllipse(
                $detailPen,
                [Single]($hubX - $hubRadius),
                [Single]($hubY - $hubRadius),
                [Single]($hubRadius * 2.0),
                [Single]($hubRadius * 2.0))
        }
        finally {
            $detailPen.Dispose()
            $arcPen.Dispose()
        }
    }
    catch {
        $bitmap.Dispose()
        throw
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-GaugeBitmap -Size $size

    try {
        [Byte[]]$frameData = if ($size -ge 128) {
            Convert-BitmapToPngFrame -Bitmap $bitmap
        }
        else {
            Convert-BitmapToIconFrame -Bitmap $bitmap
        }

        $frames += [PSCustomObject]@{
            Size = $size
            Data = $frameData
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutputPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$fileStream = [System.IO.File]::Open(
    $resolvedOutputPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = New-Object System.IO.BinaryWriter($fileStream)

try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([Byte]$dimension)
        $writer.Write([Byte]$dimension)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Data.Length)
        $writer.Write([UInt32]$offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([Byte[]]$frame.Data)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

$resolvedMasterPngPath = [System.IO.Path]::GetFullPath($MasterPngPath)
$masterPngDirectory = [System.IO.Path]::GetDirectoryName($resolvedMasterPngPath)
[System.IO.Directory]::CreateDirectory($masterPngDirectory) | Out-Null
$masterBitmap = New-GaugeBitmap -Size 1024
try {
    $masterBitmap.Save(
        $resolvedMasterPngPath,
        [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $masterBitmap.Dispose()
}

Write-Output "Generated: $resolvedOutputPath"
Write-Output "Generated: $resolvedMasterPngPath"
