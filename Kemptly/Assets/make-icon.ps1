# Generates the Kemptly logo: Assets\Kemptly.ico (16-256 px) and Assets\logo.png (256 px).
# Usage: powershell -ExecutionPolicy Bypass -File Assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-Sparkle([float]$cx, [float]$cy, [float]$r) {
    # 4-point star with concave sides
    $k = $r * 0.16
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddBezier($cx, $cy - $r, $cx + $k, $cy - $k, $cx + $k, $cy - $k, $cx + $r, $cy)
    $p.AddBezier($cx + $r, $cy, $cx + $k, $cy + $k, $cx + $k, $cy + $k, $cx, $cy + $r)
    $p.AddBezier($cx, $cy + $r, $cx - $k, $cy + $k, $cx - $k, $cy + $k, $cx - $r, $cy)
    $p.AddBezier($cx - $r, $cy, $cx - $k, $cy - $k, $cx - $k, $cy - $k, $cx, $cy - $r)
    $p.CloseFigure()
    return $p
}

function New-Logo([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square tile with a blue -> cyan gradient
    $m = [Math]::Max(0.5, $s * 0.03)
    $w = $s - 2 * $m
    $rad = $w * 0.24
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($m, $m, $rad * 2, $rad * 2, 180, 90)
    $tile.AddArc($m + $w - $rad * 2, $m, $rad * 2, $rad * 2, 270, 90)
    $tile.AddArc($m + $w - $rad * 2, $m + $w - $rad * 2, $rad * 2, $rad * 2, 0, 90)
    $tile.AddArc($m, $m + $w - $rad * 2, $rad * 2, $rad * 2, 90, 90)
    $tile.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s),
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235), [System.Drawing.Color]::FromArgb(255, 6, 182, 212))
    $g.FillPath($grad, $tile)

    # Soft highlight on the upper half
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, -1), (New-Object System.Drawing.PointF 0, ($s + 1)),
        [System.Drawing.Color]::FromArgb(45, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($hl, $tile)

    # Sparkles: one big, two small ("freshly tidied")
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPath($white, (New-Sparkle ($s * 0.44) ($s * 0.56) ($s * 0.30)))
    if ($s -ge 24) {
        $g.FillPath($white, (New-Sparkle ($s * 0.74) ($s * 0.28) ($s * 0.12)))
        $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 255, 255, 255))
        $g.FillPath($soft, (New-Sparkle ($s * 0.77) ($s * 0.70) ($s * 0.07)))
    }
    $g.Dispose()
    return $bmp
}

$dir = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
# Classic 32-bit DIB icon entry (best compatibility for small sizes)
function Get-DibBytes([System.Drawing.Bitmap]$b) {
    $s = $b.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $b.LockBits($rect, 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $b.UnlockBits($data)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($s * $s * 4)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($raw, $y * $stride, $s * 4) }   # bottom-up BGRA
    $maskRow = [Math]::Ceiling($s / 32) * 4
    $w.Write((New-Object byte[] ($maskRow * $s)))                              # empty AND mask
    $w.Flush()
    return , $ms.ToArray()
}

$pngs = foreach ($sz in $sizes) {
    $b = New-Logo $sz
    if ($sz -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $b.Save((Join-Path $dir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    }
    else { $bytes = Get-DibBytes $b }
    $b.Dispose()
    , $bytes
}

# ICO container (DIB entries, PNG for 256 px)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'Kemptly.ico'), $out.ToArray())
"Wrote Kemptly.ico and logo.png"
