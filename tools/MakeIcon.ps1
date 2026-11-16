# Generates src/LocalVault/Assets/app.ico with a frame for every size Windows requests at
# 100% / 125% / 150% / 175% / 200% scaling, so the shell never has to resample (which blurs).
# Frames <= 128 px are 32-bit BMP DIBs (most reliable in Explorer/Start); 256 is PNG.
# Run with Windows PowerShell 5.1:  powershell -ExecutionPolicy Bypass -File tools\MakeIcon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\LocalVault\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 32.0
    # Snap a 32-unit design coordinate to a whole pixel so straight edges land on pixel boundaries.
    function P([double]$v) { [float][math]::Round($v * $s) }

    # Shield: flat top and straight sides snapped to pixels, curved bottom.
    $l = P 4; $r = P 28; $t = P 2; $shoulder = P 6; $mid = P 16; $sideEnd = P 15; $bottom = [float]($size - [math]::Max(1, [math]::Round(1.5 * $s)))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLine($mid, $t, $r, $shoulder)
    $path.AddLine($r, $shoulder, $r, $sideEnd)
    $path.AddBezier($r, $sideEnd, $r, (P 23), (P 22), (P 28), $mid, $bottom)
    $path.AddBezier($mid, $bottom, (P 10), (P 28), $l, (P 23), $l, $sideEnd)
    $path.AddLine($l, $sideEnd, $l, $shoulder)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
        ([System.Drawing.Color]::FromArgb(255, 0x4F, 0x94, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x40, 0xAF)), 70
    $g.FillPath($brush, $path)

    # Thin darker rim adds definition against light and dark backgrounds (skip at tiny sizes).
    if ($size -ge 24) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(140, 0x17, 0x2E, 0x7A)), ([float][math]::Max(1, $size / 48.0))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($pen, $path)
    }

    # Keyhole: circle + tapered stem, sized to whole pixels and centred.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $d = [math]::Max(4, [math]::Round(7.5 * $s))
    $cx = $size / 2.0
    $cy = [math]::Round(12.5 * $s)
    $g.FillEllipse($white, [float]($cx - $d / 2.0), [float]($cy - $d / 2.0), [float]$d, [float]$d)
    $stemTop = [float]($cy + $d * 0.2)
    $stemBottom = [float][math]::Round(23 * $s)
    $topHalf = [math]::Max(1.0, $d * 0.22)
    $botHalf = [math]::Max(1.5, $d * 0.36)
    $kp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $kp.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([float]($cx - $topHalf)), $stemTop),
        (New-Object System.Drawing.PointF ([float]($cx + $topHalf)), $stemTop),
        (New-Object System.Drawing.PointF ([float]($cx + $botHalf)), $stemBottom),
        (New-Object System.Drawing.PointF ([float]($cx - $botHalf)), $stemBottom)))
    $g.FillPath($white, $kp)
    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

function Get-DibBytes($bmp) {
    # BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bpp AND mask (all zero: alpha channel is used).
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([math]::Ceiling($w / 32.0) * 4)
    $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($w * $h * 4 + $maskStride * $h))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $bw.Write($row)
    }
    $bmp.UnlockBits($data)
    $bw.Write((New-Object byte[] ($maskStride * $h)))
    $bw.Flush()
    return ,$ms.ToArray()
}

# 100%: 16 24 32 48 256 | 125%: 20 30 40 60 | 150%: 24 36 48 72 | 175%: 28 42 56 84 | 200%: 32 48 64 96
$sizes = 16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 60, 64, 72, 80, 84, 96, 128, 256
$images = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    if ($sz -le 128) { ,(Get-DibBytes $bmp) } else { ,(Get-PngBytes $bmp) }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Close()

# Preview sheet (each frame at 1:1) for visual checking.
$preview = Join-Path $PSScriptRoot 'icon-preview.png'
$sheet = New-Object System.Drawing.Bitmap 900, 300
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 243, 243))), 0, 150, 900, 150)
$x = 8
foreach ($sz in $sizes) {
    if ($sz -gt 128) { continue }
    $ico = New-Object System.Drawing.Icon $out, $sz, $sz
    $b = $ico.ToBitmap()
    $g.DrawImageUnscaled($b, $x, 10); $g.DrawImageUnscaled($b, $x, 160)
    $x += $sz + 8; $b.Dispose(); $ico.Dispose()
}
$g.Dispose(); $sheet.Save($preview); $sheet.Dispose()
Write-Host "Wrote $out ($($sizes.Count) sizes) and $preview"

