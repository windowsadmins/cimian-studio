#requires -Version 7.0
# Build Assets/AppIcon/AppIcon.ico from Assets/AppIcon/MunkiStudio.source.png.
#
# Emits a Vista-style PNG-encoded multi-resolution ICO (16/24/32/48/64/128/256).
# Run after replacing the source PNG to refresh the embedded icon.

[CmdletBinding()]
param(
    [string]$SourcePng = (Join-Path $PSScriptRoot '..\src\CimianStudio\Assets\AppIcon\MunkiStudio.source.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot '..\src\CimianStudio\Assets\AppIcon\AppIcon.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)

$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourcePng))
try {
    $pngs = foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($source, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
            } finally { $g.Dispose() }
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
        } finally { $bmp.Dispose() }
    }
}
finally { $source.Dispose() }

# ICO layout: 6-byte ICONDIR + N * 16-byte ICONDIRENTRY + N image blobs.
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $icoStream
try {
    $writer.Write([uint16]0)             # reserved
    $writer.Write([uint16]1)             # type = ICO
    $writer.Write([uint16]$pngs.Count)   # entry count

    $offset = 6 + (16 * $pngs.Count)
    foreach ($p in $pngs) {
        # width/height are stored as 1 byte; 0 means 256
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
        $writer.Write([byte]$dim)        # width
        $writer.Write([byte]$dim)        # height
        $writer.Write([byte]0)           # palette colors
        $writer.Write([byte]0)           # reserved
        $writer.Write([uint16]1)         # color planes
        $writer.Write([uint16]32)        # bits per pixel
        $writer.Write([uint32]$p.Bytes.Length)  # image data size
        $writer.Write([uint32]$offset)   # offset to image data
        $offset += $p.Bytes.Length
    }

    foreach ($p in $pngs) {
        $writer.Write($p.Bytes)
    }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $OutputIco)).Path + '\' + (Split-Path -Leaf $OutputIco), $icoStream.ToArray())
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

Write-Host "Wrote $OutputIco ($(([System.IO.FileInfo]$OutputIco).Length) bytes, sizes: $($sizes -join ', '))"
