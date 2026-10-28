# Generates src\OpenSweep\Assets\OpenSweep.ico (original artwork: sparkle on a green/blue tile).
# Run with Windows PowerShell:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\OpenSweep\Assets\OpenSweep.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Star([float]$cx, [float]$cy, [float]$r, [float]$inner) {
    $pts = New-Object 'System.Drawing.PointF[]' 8
    for ($i = 0; $i -lt 8; $i++) {
        $angle = [Math]::PI / 4 * $i - [Math]::PI / 2
        $rad = if ($i % 2 -eq 0) { $r } else { $r * $inner }
        $pts[$i] = New-Object System.Drawing.PointF ($cx + $rad * [Math]::Cos($angle)), ($cy + $rad * [Math]::Sin($angle))
    }
    return , $pts
}

$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $s / 256.0
    $rect = New-Object System.Drawing.RectangleF (8 * $k), (8 * $k), (240 * $k), (240 * $k)
    $radius = 56 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 16, 185, 129)), ([System.Drawing.Color]::FromArgb(255, 14, 116, 220)), 45.0
    $g.FillPath($brush, $path)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($white, (New-Star (118 * $k) (138 * $k) (86 * $k) 0.28))
    $g.FillPolygon($white, (New-Star (192 * $k) (64 * $k) (34 * $k) 0.30))
    if ($s -ge 32) { $g.FillPolygon($white, (New-Star (196 * $k) (196 * $k) (18 * $k) 0.32)) }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($s, $ms.ToArray())
    $g.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Flush(); $bw.Dispose()
Write-Output "Icon written: $out"
