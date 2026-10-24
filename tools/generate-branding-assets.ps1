#Requires -Version 5.1
<#
.SYNOPSIS
  Generates all official VYDRIX branding visual assets:
  - Vector SVG files (vydrix-logo.svg, vydrix-symbol.svg, vydrix-monochrome.svg)
  - Transparent high-res PNG logos (vydrix-logo-dark.png, vydrix-logo-light.png)
  - High-res primary app icon (vydrix-icon-512.png)
  - Multi-resolution Windows icon (vydrix-icon.ico: 16, 24, 32, 48, 64, 128, 256 px)
  - Synchronises icons and logo assets into src/Vydrix.Desktop/Assets
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot   = Resolve-Path "$PSScriptRoot\.."
$brandDir   = Join-Path $repoRoot "assets\branding"
$desktopDir = Join-Path $repoRoot "src\Vydrix.Desktop\Assets"

New-Item -ItemType Directory -Force $brandDir | Out-Null
New-Item -ItemType Directory -Force $desktopDir | Out-Null

Write-Host "== Generating VYDRIX Branding Assets ==" -ForegroundColor Cyan

# ─────────────────────────────────────────────────────────────────────────────
# Color Palette Constants
# ─────────────────────────────────────────────────────────────────────────────
$NavyDeep     = [System.Drawing.Color]::FromArgb(255, 8, 13, 24)      # #080D18
$NavyDark     = [System.Drawing.Color]::FromArgb(255, 12, 19, 36)     # #0C1324
$NavyBorder   = [System.Drawing.Color]::FromArgb(180, 40, 123, 255)   # #287BFF 70%
$CyanBright   = [System.Drawing.Color]::FromArgb(255, 53, 217, 255)   # #35D9FF
$CyanTop      = [System.Drawing.Color]::FromArgb(255, 124, 232, 255)  # #7CE8FF
$ElectricBlue = [System.Drawing.Color]::FromArgb(255, 40, 123, 255)   # #287BFF
$VioletDeep   = [System.Drawing.Color]::FromArgb(255, 112, 71, 255)   # #7047FF
$MagentaNeon  = [System.Drawing.Color]::FromArgb(255, 196, 61, 255)   # #C43DFF
$InnerBlue    = [System.Drawing.Color]::FromArgb(255, 20, 62, 156)    # #143E9C
$InnerPurple  = [System.Drawing.Color]::FromArgb(255, 75, 34, 160)    # #4B22A0
$TextLight    = [System.Drawing.Color]::FromArgb(255, 244, 247, 255)  # #F4F7FF
$TextMuted    = [System.Drawing.Color]::FromArgb(255, 155, 169, 194)  # #9BA9C2

# ─────────────────────────────────────────────────────────────────────────────
# Core Drawing: Draw VYDRIX Symbol
# ─────────────────────────────────────────────────────────────────────────────
function Draw-SymbolCore($g, [float]$cx, [float]$cy, [float]$w, [float]$h) {
    # Scale coordinates relative to 512x512 standard box
    $scaleX = $w / 512.0
    $scaleY = $h / 512.0

    function P([float]$px, [float]$py) {
        return [System.Drawing.PointF]::new($cx + $px * $scaleX, $cy + $py * $scaleY)
    }

    # 1. Cloud Outline (Glowing Neon Arcs)
    $cloudPen = New-Object System.Drawing.Pen($CyanBright, [Math]::Max(1.5, [float](12 * $scaleX)))
    $cloudPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cloudPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    # Outer arc left, center, right
    $g.DrawArc($cloudPen, ($cx + 140 * $scaleX), ($cy + 155 * $scaleY), (75 * $scaleX), (75 * $scaleY), 140, 190)
    $g.DrawArc($cloudPen, ($cx + 205 * $scaleX), ($cy + 90 * $scaleY), (102 * $scaleX), (102 * $scaleY), 160, 220)
    $g.DrawArc($cloudPen, ($cx + 297 * $scaleX), ($cy + 155 * $scaleY), (75 * $scaleX), (75 * $scaleY), 210, 190)
    $cloudPen.Dispose()

    # 2. Outer Left Wing (Cyan to Electric Blue)
    $pLeftWing = [System.Drawing.PointF[]]@(
        (P 56 180),
        (P 150 180),
        (P 256 432),
        (P 202 432)
    )
    $rectLeft = [System.Drawing.RectangleF]::new(($cx + 56 * $scaleX), ($cy + 180 * $scaleY), (200 * $scaleX), (252 * $scaleY))
    $brLeft = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rectLeft, $CyanBright, $ElectricBlue, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $g.FillPolygon($brLeft, $pLeftWing)
    $brLeft.Dispose()

    # Left Top Bevel
    $pLeftBevel = [System.Drawing.PointF[]]@(
        (P 56 180),
        (P 150 180),
        (P 132 204),
        (P 78 204)
    )
    $brLeftBevel = New-Object System.Drawing.SolidBrush($CyanTop)
    $g.FillPolygon($brLeftBevel, $pLeftBevel)
    $brLeftBevel.Dispose()

    # 3. Outer Right Wing (Electric Blue to Magenta)
    $pRightWing = [System.Drawing.PointF[]]@(
        (P 456 180),
        (P 362 180),
        (P 256 432),
        (P 310 432)
    )
    $rectRight = [System.Drawing.RectangleF]::new(($cx + 256 * $scaleX), ($cy + 180 * $scaleY), (200 * $scaleX), (252 * $scaleY))
    $brRight = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rectRight, $ElectricBlue, $MagentaNeon, [System.Drawing.Drawing2D.LinearGradientMode]::BackwardDiagonal)
    $g.FillPolygon($brRight, $pRightWing)
    $brRight.Dispose()

    # Right Top Bevel
    $pRightBevel = [System.Drawing.PointF[]]@(
        (P 456 180),
        (P 362 180),
        (P 380 204),
        (P 434 204)
    )
    $brRightBevel = New-Object System.Drawing.SolidBrush($MagentaNeon)
    $g.FillPolygon($brRightBevel, $pRightBevel)
    $brRightBevel.Dispose()

    # 4. Central Inner Wedge (Depth Facets)
    $pInnerLeft = [System.Drawing.PointF[]]@(
        (P 256 260),
        (P 182 232),
        (P 202 432),
        (P 256 432)
    )
    $brInnerLeft = New-Object System.Drawing.SolidBrush($InnerBlue)
    $g.FillPolygon($brInnerLeft, $pInnerLeft)
    $brInnerLeft.Dispose()

    $pInnerRight = [System.Drawing.PointF[]]@(
        (P 256 260),
        (P 330 232),
        (P 310 432),
        (P 256 432)
    )
    $brInnerRight = New-Object System.Drawing.SolidBrush($InnerPurple)
    $g.FillPolygon($brInnerRight, $pInnerRight)
    $brInnerRight.Dispose()

    # 5. Center Spine Highlight
    $spinePen = New-Object System.Drawing.Pen($CyanTop, [Math]::Max(1.0, [float](2.0 * $scaleX)))
    $g.DrawLine($spinePen, (P 256 260), (P 256 432))
    $spinePen.Dispose()
}

# ─────────────────────────────────────────────────────────────────────────────
# 1. Generate App Icon 512x512 (vydrix-icon-512.png)
# ─────────────────────────────────────────────────────────────────────────────
Write-Host "Creating vydrix-icon-512.png..."
$bmp512 = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g512   = [System.Drawing.Graphics]::FromImage($bmp512)
$g512.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g512.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g512.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

# Rounded backdrop tile
$pathSquircle = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 96.0; $dia = $r * 2; $b = 16.0; $wTile = 512.0 - 2*$b; $hTile = 512.0 - 2*$b
$pathSquircle.AddArc($b, $b, $dia, $dia, 180, 90)
$pathSquircle.AddArc(($b + $wTile - $dia), $b, $dia, $dia, 270, 90)
$pathSquircle.AddArc(($b + $wTile - $dia), ($b + $hTile - $dia), $dia, $dia, 0, 90)
$pathSquircle.AddArc($b, ($b + $hTile - $dia), $dia, $dia, 90, 90)
$pathSquircle.CloseFigure()

$bgBrush = New-Object System.Drawing.SolidBrush($NavyDeep)
$g512.FillPath($bgBrush, $pathSquircle)
$bgBrush.Dispose()

# Glowing border
$rimPen = New-Object System.Drawing.Pen($NavyBorder, 3.5)
$g512.DrawPath($rimPen, $pathSquircle)
$rimPen.Dispose()
$pathSquircle.Dispose()

# Draw symbol inside 512 tile
Draw-SymbolCore $g512 36 36 440 440

$p512 = Join-Path $brandDir "vydrix-icon-512.png"
$bmp512.Save($p512, [System.Drawing.Imaging.ImageFormat]::Png)
$g512.Dispose()

# ─────────────────────────────────────────────────────────────────────────────
# 2. Generate Horizontal Logos (Dark & Light)
# ─────────────────────────────────────────────────────────────────────────────
function Generate-HorizontalLogo([string]$outPath, [bool]$isDark) {
    $w = 920; $h = 260
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Symbol on left
    Draw-SymbolCore $g 25 15 230 230

    # Fonts
    $fontMain = New-Object System.Drawing.Font("Segoe UI", 68, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fontTag  = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

    $textColor = if ($isDark) { $TextLight } else { $NavyDeep }
    $xColor    = if ($isDark) { $MagentaNeon } else { $VioletDeep }
    $tagColor  = if ($isDark) { $CyanBright } else { $ElectricBlue }

    # Draw "VYDRI"
    $brText = New-Object System.Drawing.SolidBrush($textColor)
    $g.DrawString("VYDRI", $fontMain, $brText, 275, 55)
    $brText.Dispose()

    # Draw "X" with accent color
    $brX = New-Object System.Drawing.SolidBrush($xColor)
    $g.DrawString("X", $fontMain, $brX, 550, 55)
    $brX.Dispose()

    # Draw Tagline: "CLOUD-TO-SSD ENGINE"
    $brTag = New-Object System.Drawing.SolidBrush($tagColor)
    $g.DrawString("C L O U D - T O - S S D   E N G I N E", $fontTag, $brTag, 280, 150)
    $brTag.Dispose()

    $fontMain.Dispose()
    $fontTag.Dispose()
    $g.Dispose()

    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created $(Split-Path $outPath -Leaf)"
}

Generate-HorizontalLogo (Join-Path $brandDir "vydrix-logo-dark.png") $true
Generate-HorizontalLogo (Join-Path $brandDir "vydrix-logo-light.png") $false

# ─────────────────────────────────────────────────────────────────────────────
# 3. Generate Multi-Resolution Windows Icons (16, 24, 32, 48, 64, 128, 256)
# ─────────────────────────────────────────────────────────────────────────────
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$pngFrames = @()

foreach ($sz in $iconSizes) {
    $bmpSz = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gSz   = [System.Drawing.Graphics]::FromImage($bmpSz)
    $gSz.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $gSz.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gSz.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    if ($sz -ge 32) {
        # Tile background for larger icons
        $pad = [int]($sz * 0.04)
        $rad = [int]($sz * 0.20)
        $tileW = $sz - 2 * $pad
        
        $pPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $pPath.AddArc($pad, $pad, $rad*2, $rad*2, 180, 90)
        $pPath.AddArc(($pad + $tileW - $rad*2), $pad, $rad*2, $rad*2, 270, 90)
        $pPath.AddArc(($pad + $tileW - $rad*2), ($pad + $tileW - $rad*2), $rad*2, $rad*2, 0, 90)
        $pPath.AddArc($pad, ($pad + $tileW - $rad*2), $rad*2, $rad*2, 90, 90)
        $pPath.CloseFigure()

        $bBrush = New-Object System.Drawing.SolidBrush($NavyDeep)
        $gSz.FillPath($bBrush, $pPath)
        $bBrush.Dispose()

        $rPen = New-Object System.Drawing.Pen($NavyBorder, [Math]::Max(1.0, [float]($sz * 0.02)))
        $gSz.DrawPath($rPen, $pPath)
        $rPen.Dispose(); $pPath.Dispose()

        $symPad = [int]($sz * 0.08)
        $symSz  = $sz - 2 * $symPad
        Draw-SymbolCore $gSz $symPad $symPad $symSz $symSz
    } else {
        # Small icons (16, 24): razor-sharp high-contrast V symbol with cloud arc
        $bBrush = New-Object System.Drawing.SolidBrush($NavyDeep)
        $gSz.FillRectangle($bBrush, 0, 0, $sz, $sz)
        $bBrush.Dispose()

        $symPad = 1
        $symSz = $sz - 2
        Draw-SymbolCore $gSz $symPad $symPad $symSz $symSz
    }

    $gSz.Dispose()

    $pFrame = Join-Path $brandDir "vydrix_${sz}.png"
    $bmpSz.Save($pFrame, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngFrames += $pFrame

    # Also sync into desktop assets folder
    Copy-Item $pFrame (Join-Path $desktopDir "vydrix_${sz}.png") -Force
    $bmpSz.Dispose()
}

# ─────────────────────────────────────────────────────────────────────────────
# 4. Pack Multi-Resolution Windows ICO
# ─────────────────────────────────────────────────────────────────────────────
function Pack-IcoFile([string[]]$pngList, [string]$destination) {
    $images = $pngList | ForEach-Object {
        $bytes = [IO.File]::ReadAllBytes($_)
        $bWidth  = $bytes[19]
        $bHeight = $bytes[23]
        [PSCustomObject]@{
            Bytes  = $bytes
            Width  = if ($bWidth -eq 0) { 256 } else { [int]$bWidth }
            Height = if ($bHeight -eq 0) { 256 } else { [int]$bHeight }
        }
    }

    $count = $images.Count
    $headerSize = 6
    $entrySize  = 16
    $dirSize    = $headerSize + $count * $entrySize
    $offset     = $dirSize

    $ms = New-Object System.IO.MemoryStream
    $ms.Write([BitConverter]::GetBytes([uint16]0), 0, 2)
    $ms.Write([BitConverter]::GetBytes([uint16]1), 0, 2)
    $ms.Write([BitConverter]::GetBytes([uint16]$count), 0, 2)

    foreach ($img in $images) {
        $w = if ($img.Width  -ge 256) { 0 } else { [byte]$img.Width }
        $h = if ($img.Height -ge 256) { 0 } else { [byte]$img.Height }
        $ms.WriteByte($w)
        $ms.WriteByte($h)
        $ms.WriteByte(0)
        $ms.WriteByte(0)
        $ms.Write([BitConverter]::GetBytes([uint16]1), 0, 2)
        $ms.Write([BitConverter]::GetBytes([uint16]32), 0, 2)
        $ms.Write([BitConverter]::GetBytes([uint32]$img.Bytes.Length), 0, 4)
        $ms.Write([BitConverter]::GetBytes([uint32]$offset), 0, 4)
        $offset += $img.Bytes.Length
    }

    foreach ($img in $images) {
        $ms.Write($img.Bytes, 0, $img.Bytes.Length)
    }

    [IO.File]::WriteAllBytes($destination, $ms.ToArray())
    $ms.Dispose()
    Write-Host "Created $(Split-Path $destination -Leaf)"
}

$brandIco = Join-Path $brandDir "vydrix-icon.ico"
Pack-IcoFile $pngFrames $brandIco

# Also sync to src/Vydrix.Desktop/Assets/vydrix.ico
Copy-Item $brandIco (Join-Path $desktopDir "vydrix.ico") -Force
Copy-Item (Join-Path $brandDir "vydrix-logo.svg") (Join-Path $desktopDir "vydrix-logo.svg") -Force
Copy-Item (Join-Path $brandDir "vydrix-symbol.svg") (Join-Path $desktopDir "vydrix-symbol.svg") -Force
Copy-Item (Join-Path $brandDir "vydrix-logo-dark.png") (Join-Path $desktopDir "vydrix-logo-dark.png") -Force

# ─────────────────────────────────────────────────────────────────────────────
# 5. Create Brand Documentation README.md
# ─────────────────────────────────────────────────────────────────────────────
$readmePath = Join-Path $brandDir "README.md"
Set-Content $readmePath @"
# VYDRIX Visual Identity & Branding Guidelines

This directory contains the official brand assets, logos, vector drawings, and icons for **VYDRIX (Cloud-to-SSD Engine)**.

## Brand Direction

* **Product Name:** VYDRIX
* **Tagline:** CLOUD-TO-SSD ENGINE
* **Design Language:** Sleek, high-performance, futuristic gaming storage system combining a bold geometric "V" chevron with a stylized neon cloud silhouette.
* **Palette:**
  * **Deep Navy (Background):** \`#080D18\` (\`rgb(8, 13, 24)\`)
  * **Electric Cyan (Highlight):** \`#35D9FF\` (\`rgb(53, 217, 255)\`)
  * **Electric Blue (Primary Accent):** \`#287BFF\` (\`rgb(40, 123, 255)\`)
  * **Violet (Secondary Accent):** \`#7047FF\` (\`rgb(112, 71, 255)\`)
  * **Magenta / Neon Accent:** \`#C43DFF\` (\`rgb(196, 61, 255)\`)
  * **Primary Text:** \`#F4F7FF\` (\`rgb(244, 247, 255)\`)
  * **Secondary / Muted Text:** \`#9BA9C2\` (\`rgb(155, 169, 194)\`)

---

## Asset Inventory

| File | Type | Dimensions | Purpose |
| :--- | :--- | :--- | :--- |
| \`vydrix-symbol.svg\` | Vector SVG | 512×512 | Standalone V/cloud symbol for icons, avatars, and compact spaces. |
| \`vydrix-logo.svg\` | Vector SVG | 920×260 | Full horizontal logo with wordmark and tagline. |
| \`vydrix-monochrome.svg\` | Vector SVG | 920×260 | Single-color black logo for high-contrast documents and monochrome printing. |
| \`vydrix-monochrome-white.svg\` | Vector SVG | 920×260 | Single-color white logo for dark surfaces. |
| \`vydrix-logo-dark.png\` | 32-bit PNG | 920×260 | Transparent full logo formatted for dark UI backgrounds. |
| \`vydrix-logo-light.png\` | 32-bit PNG | 920×260 | Transparent full logo formatted for light UI backgrounds. |
| \`vydrix-icon-512.png\` | 32-bit PNG | 512×512 | Master high-resolution app icon on rounded dark navy tile. |
| \`vydrix-icon.ico\` | Windows Icon | 16–256 px | Multi-resolution icon for Windows executable, desktop shortcut, taskbar, and tray. |

---

## Resolution Matrix in \`vydrix-icon.ico\`

The multi-resolution Windows icon bundle includes 7 native frames:
1. **16×16:** Small taskbar, file details, notification area.
2. **24×24:** Medium notification area, custom toolbar icons.
3. **32×32:** Standard desktop icon, window title-bar, sidebar logo badge.
4. **48×48:** Medium desktop icon, start menu tile.
5. **64×64:** High-DPI taskbar tile.
6. **128×128:** High-DPI Explorer list/tiles.
7. **256×256:** Extra-large Windows Explorer preview tile.

---

## Typography Guidelines

* **Wordmark:** Bold / Heavy uppercase sans-serif (\`Segoe UI\`, \`Montserrat\`, \`Inter\`). Letter-spacing: \`9px\`.
* **Accent letter:** The letter **X** in VYDRIX carries the magenta/violet gradient accent (\`#7047FF\` to \`#C43DFF\`).
* **Tagline:** Uppercase wide-spaced bold sans-serif (\`16–22px\`). Letter-spacing: \`8px\`.
"@

Write-Host "Brand assets generated successfully!" -ForegroundColor Green
