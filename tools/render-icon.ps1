# Regenerate the checked-in PNG/ICO from the simple, editable SVG. Windows build tool only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../assets'))
[xml]$svg = Get-Content -Raw -LiteralPath (Join-Path $root 'icon.svg')
$canvas = [Drawing.Bitmap]::new(1024, 1024)
$graphics = [Drawing.Graphics]::FromImage($canvas)
try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(4, 4)
    foreach ($shape in $svg.DocumentElement.ChildNodes) {
        if ($shape.LocalName -eq 'title') { continue }
        $pen = $null; $brush = $null
        try {
            if ($shape.HasAttribute('stroke')) { $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($shape.GetAttribute('stroke')), [float]$shape.GetAttribute('stroke-width')) }
            if ($shape.LocalName -eq 'polygon') {
                [Drawing.PointF[]]$points = @($shape.GetAttribute('points').Split(' ') | ForEach-Object { $pair = $_.Split(','); [Drawing.PointF]::new([float]$pair[0], [float]$pair[1]) })
                $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($shape.GetAttribute('fill')))
                $graphics.FillPolygon($brush, $points)
                if ($pen) { $graphics.DrawPolygon($pen, $points) }
            } elseif ($shape.LocalName -eq 'line') {
                $graphics.DrawLine($pen, [float]$shape.x1, [float]$shape.y1, [float]$shape.x2, [float]$shape.y2)
            } else { throw "Unsupported SVG element: $($shape.LocalName)" }
        } finally { if ($pen) { $pen.Dispose() }; if ($brush) { $brush.Dispose() } }
    }
    $frames = @()
    foreach ($size in @(16,24,32,48,64,128,256,512)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $scaled = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $scaled.DrawImage($canvas, 0, 0, $size, $size)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            if ($size -eq 512) { [IO.File]::WriteAllBytes((Join-Path $root 'icon.png'), $bytes) }
            else { $frames += @{ Size = $size; Bytes = $bytes } }
        } finally { $stream.Dispose(); $scaled.Dispose(); $bitmap.Dispose() }
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $root 'icon.ico')))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose() }
} finally { $graphics.Dispose(); $canvas.Dispose() }
