# Generates the branded installer bitmaps (banner.bmp 493x58, dialog.bmp 493x312) from the app logo.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$logo = [System.Drawing.Image]::FromFile((Join-Path $dir '..\WinTidy\Assets\logo.png'))

function New-Gradient($w, $h) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $w, $h),
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235), [System.Drawing.Color]::FromArgb(255, 6, 182, 212))
}

# Dialog (welcome/finish): left 164 px branded panel, the rest stays white for the dialog text.
$d = New-Object System.Drawing.Bitmap 493, 312
$g = [System.Drawing.Graphics]::FromImage($d)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$g.FillRectangle((New-Gradient 164 312), 0, 0, 164, 312)
$g.DrawImage($logo, 34, 70, 96, 96)
$font = New-Object System.Drawing.Font 'Segoe UI Semibold', 17
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString('WinTidy', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 180, 164, 40), $fmt)
$small = New-Object System.Drawing.Font 'Segoe UI', 8.5
$g.DrawString("Clean up, speed up`nand check your PC", $small, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))), (New-Object System.Drawing.RectangleF 0, 214, 164, 40), $fmt)
$g.Dispose()
$d.Save((Join-Path $dir 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)

# Banner (top of inner pages): white with the logo on the right.
$b = New-Object System.Drawing.Bitmap 493, 58
$g = [System.Drawing.Graphics]::FromImage($b)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
$g.Clear([System.Drawing.Color]::White)
$g.DrawImage($logo, 493 - 52, 7, 44, 44)
$g.Dispose()
$b.Save((Join-Path $dir 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
"Wrote dialog.bmp and banner.bmp"
