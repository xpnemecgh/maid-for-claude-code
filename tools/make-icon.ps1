# Generates src/Maid/Assets/maid.ico: a 2x2 grid of terminal panes, one of them "live".
# Usage: powershell -File tools/make-icon.ps1

Add-Type -AssemblyName System.Drawing

$output = Join-Path $PSScriptRoot "..\src\Maid\Assets\maid.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [Math]::Round($size * 0.06))
    $gap = [Math]::Max(1, [Math]::Round($size * 0.08))
    $tile = ($size - 2 * $pad - $gap) / 2
    $radius = [Math]::Max(1, $tile * 0.24)

    $live = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xE0, 0x82, 0x5F))
    $idle = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x56, 0x61, 0x78))

    foreach ($row in 0, 1) {
        foreach ($col in 0, 1) {
            $x = $pad + $col * ($tile + $gap)
            $y = $pad + $row * ($tile + $gap)
            $shape = New-RoundedRect $x $y $tile $tile $radius
            $brush = $idle
            if ($row -eq 0 -and $col -eq 0) { $brush = $live }
            $g.FillPath($brush, $shape)
            $shape.Dispose()
        }
    }

    # A prompt chevron on the live pane, once there are enough pixels for it to read.
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x1A, 0x0F, 0x0A)), ($tile * 0.13)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $cx = $pad + $tile * 0.34
        $cy = $pad + $tile * 0.5
        $arm = $tile * 0.2
        $points = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($cx), ($cy - $arm)),
            (New-Object System.Drawing.PointF ($cx + $arm), ($cy)),
            (New-Object System.Drawing.PointF ($cx), ($cy + $arm)))
        $g.DrawLines($pen, $points)
        $g.DrawLine($pen, [single]($cx + $arm * 1.45), [single]($cy + $arm), [single]($cx + $arm * 2.35), [single]($cy + $arm))
        $pen.Dispose()
    }

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

$images = foreach ($size in $sizes) { [pscustomobject]@{ Size = $size; Bytes = (New-IconPng $size) } }

# ICO container: ICONDIR, one ICONDIRENTRY per image, then the PNG payloads.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
$writer.Flush()

New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
[System.IO.File]::WriteAllBytes($output, $file.ToArray())
"Wrote $output ($($file.Length) bytes, sizes: $($sizes -join ', '))"
