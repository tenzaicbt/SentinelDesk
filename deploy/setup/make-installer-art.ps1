<#
.SYNOPSIS
  Generates the NSIS installer artwork from the brand assets in doc/logo:
    wizard.bmp  - Welcome / Finish side panel (164x314 logical, drawn at 2x for high-DPI screens)
    header.bmp  - inner-page header image   (150x57 logical, drawn at 2x)
  Brand palette: navy #1a1a2e, orange #ff6a00, slate #6b6b80, flame #ffaa13 -> #ff6a00 -> #ff3428.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$icon = [Drawing.Image]::FromFile((Join-Path $root 'doc\logo\SentinelDesk-fire-icon.png'))

$navy   = [Drawing.Color]::FromArgb(0x1A, 0x1A, 0x2E)
$navy2  = [Drawing.Color]::FromArgb(0x2A, 0x2A, 0x48)
$orange = [Drawing.Color]::FromArgb(0xFF, 0x6A, 0x00)
$amber  = [Drawing.Color]::FromArgb(0xFF, 0xAA, 0x13)
$red    = [Drawing.Color]::FromArgb(0xFF, 0x34, 0x28)
$slate  = [Drawing.Color]::FromArgb(0x6B, 0x6B, 0x80)

function New-Canvas($w, $h) {
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    return $bmp, $g
}

function Draw-Wordmark($g, [float]$x, [float]$y, [float]$size, $inkColor, [switch]$Center, [float]$width) {
    $font = New-Object Drawing.Font 'Segoe UI', $size, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $fmt = [Drawing.StringFormat]::GenericTypographic
    $w1 = $g.MeasureString('Sentinel', $font, 2000, $fmt).Width
    $w2 = $g.MeasureString('Desk', $font, 2000, $fmt).Width
    if ($Center) { $x = $x + ($width - $w1 - $w2) / 2 }
    $g.DrawString('Sentinel', $font, (New-Object Drawing.SolidBrush $inkColor), $x, $y, $fmt)
    $g.DrawString('Desk', $font, (New-Object Drawing.SolidBrush $orange), ($x + $w1), $y, $fmt)
    $font.Dispose()
}

function Draw-Spaced($g, [string]$text, [float]$cx, [float]$y, [float]$size, $color, [float]$spacing) {
    $font = New-Object Drawing.Font 'Segoe UI Semibold', $size, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $fmt = [Drawing.StringFormat]::GenericTypographic
    $widths = $text.ToCharArray() | ForEach-Object { $g.MeasureString([string]$_, $font, 200, $fmt).Width + $(if ($_ -eq ' ') { $size * 0.3 } else { 0 }) }
    $total = ($widths | Measure-Object -Sum).Sum + $spacing * ($text.Length - 1)
    $x = $cx - $total / 2
    $brush = New-Object Drawing.SolidBrush $color
    for ($i = 0; $i -lt $text.Length; $i++) { $g.DrawString([string]$text[$i], $font, $brush, $x, $y, $fmt); $x += $widths[$i] + $spacing }
    $font.Dispose()
}

# ── wizard.bmp: navy panel, dot texture (echoes the flame's pixel grid), flame, wordmark, flame-gradient bar
$S = 2; $W = 164 * $S; $H = 314 * $S
$bmp, $g = New-Canvas $W $H
$bg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Rectangle]::new(0, 0, $W, $H)), $navy2, $navy, 90
$g.FillRectangle($bg, 0, 0, $W, $H)
# warm glow behind the flame
$glow = New-Object Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse([float](-$W * 0.35), [float]($H * 0.08), [float]($W * 1.7), [float]($W * 1.4))
$pgb = New-Object Drawing.Drawing2D.PathGradientBrush $glow
$pgb.CenterColor = [Drawing.Color]::FromArgb(70, 0xFF, 0x6A, 0x00); $pgb.SurroundColors = @([Drawing.Color]::FromArgb(0, 0xFF, 0x6A, 0x00))
$g.FillPath($pgb, $glow)
# dot grid
$dot = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(16, 255, 255, 255))
for ($yy = 10; $yy -lt $H; $yy += 22) { for ($xx = 10; $xx -lt $W; $xx += 22) { $g.FillRectangle($dot, $xx, $yy, 5, 5) } }
# flame
$iconSize = [int]($W * 0.78)
$g.DrawImage($icon, [int](($W - $iconSize) / 2), [int]($H * 0.10), $iconSize, $iconSize)
# wordmark + tagline
Draw-Wordmark $g 0 ($H * 0.62) (25 * $S) ([Drawing.Color]::White) -Center -width $W
Draw-Spaced $g 'ENDPOINT METRICS' ($W / 2) ($H * 0.62 + 36 * $S) (7.5 * $S) ([Drawing.Color]::FromArgb(0xB4, 0xB4, 0xC8)) (1.6 * $S)
# flame-gradient accent bar
$barH = 5 * $S
$bar = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Rectangle]::new(0, $H - $barH, $W, $barH)), $amber, $red, 0
$blend = New-Object Drawing.Drawing2D.ColorBlend 3
$blend.Colors = @($amber, $orange, $red); $blend.Positions = @([float]0, [float]0.5, [float]1); $bar.InterpolationColors = $blend
$g.FillRectangle($bar, 0, $H - $barH, $W, $barH)
$g.Dispose(); $bmp.Save((Join-Path $PSScriptRoot 'wizard.bmp'), [Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# ── header.bmp: white, wordmark right-aligned with the flame (MUI_HEADERIMAGE_RIGHT)
$W = 150 * $S; $H = 57 * $S
$bmp, $g = New-Canvas $W $H
$g.Clear([Drawing.Color]::White)
$is = [int](40 * $S)
$g.DrawImage($icon, $W - $is - 4 * $S, [int](($H - $is) / 2), $is, $is)
$font = New-Object Drawing.Font 'Segoe UI', (15 * $S), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
$fmt = [Drawing.StringFormat]::GenericTypographic
$w1 = $g.MeasureString('Sentinel', $font, 2000, $fmt).Width; $w2 = $g.MeasureString('Desk', $font, 2000, $fmt).Width
$x = $W - $is - 6 * $S - $w1 - $w2
Draw-Wordmark $g $x (14 * $S) (15 * $S) $navy
$tag = New-Object Drawing.Font 'Segoe UI Semibold', (6.2 * $S), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
$g.DrawString('ENDPOINT METRICS', $tag, (New-Object Drawing.SolidBrush $slate), ($x + 1 * $S), (33 * $S), $fmt)
$g.Dispose(); $bmp.Save((Join-Path $PSScriptRoot 'header.bmp'), [Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

$icon.Dispose()
Write-Host "wizard.bmp and header.bmp written to $PSScriptRoot"
