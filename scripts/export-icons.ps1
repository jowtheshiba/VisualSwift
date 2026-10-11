param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repository = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repository 'assets'
$output = Join-Path $assets 'icons'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($name in @('visualswift', 'swift-console', 'swift-library')) {
    $source = [System.Drawing.Image]::FromFile((Join-Path $assets "generated\$name.png"))
    try {
        $frames = @()
        foreach ($size in @(16, 32, 48, 64, 128, 256)) {
            $bitmap = New-Object System.Drawing.Bitmap($size, $size)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, 0, 0, $size, $size)
                $stream = New-Object System.IO.MemoryStream
                try {
                    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                    $frames += ,$stream.ToArray()
                    [System.IO.File]::WriteAllBytes((Join-Path $output "$name-$size.png"), $stream.ToArray())
                }
                finally { $stream.Dispose() }
            }
            finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
        $file = [System.IO.File]::Create((Join-Path $output "$name.ico"))
        $writer = New-Object System.IO.BinaryWriter($file)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$frames.Count)
            $offset = 6 + 16 * $frames.Count
            $sizes = @(16, 32, 48, 64, 128, 256)
            for ($index = 0; $index -lt $frames.Count; $index++) {
                $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$frames[$index].Length)
                $writer.Write([uint32]$offset)
                $offset += $frames[$index].Length
            }
            foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
        }
        finally { $writer.Dispose(); $file.Dispose() }
    }
    finally { $source.Dispose() }
}
