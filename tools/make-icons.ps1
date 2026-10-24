#Requires -Version 5.1
<#
.SYNOPSIS
    Generates VYDRIX PNG icons at multiple resolutions and combines them into a .ico file.
.DESCRIPTION
    1. Tries Inkscape to rasterise the SVG.
    2. Falls back to System.Drawing GDI+ rendering if Inkscape is not found.
    3. Packs all PNG sizes into a single ICO file using raw binary ICO format.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$svgPath  = Join-Path $PSScriptRoot '..\src\Vydrix.Desktop\Assets\logo.svg'
$outDir   = Join-Path $PSScriptRoot '..\src\Vydrix.Desktop\Assets'
$icoPath  = Join-Path $outDir 'vydrix.ico'
$sizes    = @(16, 32, 48, 64, 128, 256)

$svgPath  = [IO.Path]::GetFullPath($svgPath)
$outDir   = [IO.Path]::GetFullPath($outDir)
$icoPath  = [IO.Path]::GetFullPath($icoPath)

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
# Helper: try Inkscape export
# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function Try-Inkscape {
    $candidates = @(
        'C:\Program Files\Inkscape\bin\inkscape.exe',
        'C:\Program Files (x86)\Inkscape\bin\inkscape.exe',
        (& { $cmd = Get-Command 'inkscape' -ErrorAction SilentlyContinue; if ($cmd) { $cmd.Source } })
    ) | Where-Object { $_ -and (Test-Path $_) }

    if (-not $candidates) { return $false }
    $ink = $candidates[0]
    Write-Host "[Inkscape] Found at $ink"

    $pngs = @()
    foreach ($sz in $sizes) {
        $pngFile = Join-Path $outDir "vydrix_${sz}.png"
        & $ink --export-type=png --export-width=$sz --export-height=$sz `
               --export-filename=$pngFile $svgPath 2>&1 | Out-Null
        if (Test-Path $pngFile) {
            Write-Host "  Exported ${sz}x${sz} -> $pngFile"
            $pngs += $pngFile
        } else {
            Write-Warning "  Inkscape failed for size $sz"
        }
    }
    return ($pngs.Count -gt 0)
}

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
# Helper: GDI+ fallback â€” draw a VYDRIX-branded icon programmatically
# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function Make-Gdi {
    Add-Type -AssemblyName System.Drawing

    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz,
                   [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)

        $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        # â”€â”€ Background â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        $bgBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(255, 10, 15, 30))     # #0A0F1E
        $g.FillRectangle($bgBrush, 0, 0, $sz, $sz)

        # â”€â”€ V shape: two arms as filled polygons with gradient-ish colours â”€â”€
        # For small sizes just draw a bold "V" letter; for larger draw polygons.
        if ($sz -ge 32) {
            $pad   = [int]($sz * 0.12)
            $top   = [int]($sz * 0.12)
            $bot   = [int]($sz * 0.72)
            $mid   = [int]($sz / 2)
            $thick = [Math]::Max(2, [int]($sz * 0.18))

            # Left arm: topleft â†’ apex (mid, bot)
            $leftArm = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($pad,           $top),
                [System.Drawing.PointF]::new($pad + $thick,  $top),
                [System.Drawing.PointF]::new($mid + ($thick/2), $bot),
                [System.Drawing.PointF]::new($mid - ($thick/2), $bot)
            )
            # Right arm: topright â†’ apex
            $rightArm = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($sz - $pad - $thick, $top),
                [System.Drawing.PointF]::new($sz - $pad,          $top),
                [System.Drawing.PointF]::new($mid + ($thick/2),   $bot),
                [System.Drawing.PointF]::new($mid - ($thick/2),   $bot)
            )

            # Cyan brush for left, violet for right â€” approximates gradient
            $cyanBrush   = New-Object System.Drawing.SolidBrush(
                [System.Drawing.Color]::FromArgb(255, 0, 229, 255))   # #00E5FF
            $violetBrush = New-Object System.Drawing.SolidBrush(
                [System.Drawing.Color]::FromArgb(255, 144, 0, 255))   # #9000FF

            $g.FillPolygon($cyanBrush,   $leftArm)
            $g.FillPolygon($violetBrush, $rightArm)

            # Arc above the apex
            if ($sz -ge 48) {
                $arcPen = New-Object System.Drawing.Pen(
                    [System.Drawing.Color]::FromArgb(220, 0, 229, 255), [float][Math]::Max(1, $sz*0.025))
                $arcX = [int]($sz * 0.28)
                $arcW = [int]($sz * 0.44)
                $arcY = [int]($sz * 0.06)
                $arcH = [int]($sz * 0.20)
                $g.DrawArc($arcPen, $arcX, $arcY, $arcW, $arcH, 180, 180)
                $arcPen.Dispose()
            }

            $cyanBrush.Dispose()
            $violetBrush.Dispose()
        } else {
            # Tiny sizes: just render a bold "V" letter
            $fSize  = [float]([Math]::Max(6, $sz * 0.72))
            $font   = New-Object System.Drawing.Font('Segoe UI', $fSize,
                          [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $brush  = New-Object System.Drawing.SolidBrush(
                [System.Drawing.Color]::FromArgb(255, 0, 144, 255))   # #0090FF
            $sf     = New-Object System.Drawing.StringFormat
            $sf.Alignment          = [System.Drawing.StringAlignment]::Center
            $sf.LineAlignment      = [System.Drawing.StringAlignment]::Center
            $rect   = New-Object System.Drawing.RectangleF(0, 0, $sz, $sz)
            $g.DrawString('V', $font, $brush, $rect, $sf)
            $font.Dispose(); $brush.Dispose(); $sf.Dispose()
        }

        $bgBrush.Dispose()
        $g.Dispose()

        $pngFile = Join-Path $outDir "vydrix_${sz}.png"
        $bmp.Save($pngFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host "  [GDI+] Rendered ${sz}x${sz} -> $pngFile"
    }
}

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
# Helper: pack PNGs into a proper ICO binary
# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function Build-Ico {
    param([string[]]$PngPaths, [string]$Destination)

    # Read all PNG bytes
    $images = $PngPaths | ForEach-Object {
        [PSCustomObject]@{
            Path  = $_
            Bytes = [IO.File]::ReadAllBytes($_)
            # Parse width/height from PNG header (bytes 16-23)
            Width  = [int]([BitConverter]::ToUInt32(@(
                            (([IO.File]::ReadAllBytes($_))[19]),
                            (([IO.File]::ReadAllBytes($_))[18]),
                            (([IO.File]::ReadAllBytes($_))[17]),
                            (([IO.File]::ReadAllBytes($_))[16])
                        ), 0))
            Height = [int]([BitConverter]::ToUInt32(@(
                            (([IO.File]::ReadAllBytes($_))[23]),
                            (([IO.File]::ReadAllBytes($_))[22]),
                            (([IO.File]::ReadAllBytes($_))[21]),
                            (([IO.File]::ReadAllBytes($_))[20])
                        ), 0))
        }
    }

    $count       = $images.Count
    $headerSize  = 6              # ICONDIR header
    $dirEntrySize = 16            # per image
    $dirSize     = $headerSize + $count * $dirEntrySize
    $dataOffset  = $dirSize

    $ms = New-Object System.IO.MemoryStream

    # ICONDIR header
    $ms.Write([BitConverter]::GetBytes([uint16]0),      0, 2)  # reserved
    $ms.Write([BitConverter]::GetBytes([uint16]1),      0, 2)  # type = ICO
    $ms.Write([BitConverter]::GetBytes([uint16]$count), 0, 2)  # image count

    # Calculate offsets
    $offset = $dataOffset
    foreach ($img in $images) {
        $w = if ($img.Width  -eq 256) { 0 } else { [byte]$img.Width  }
        $h = if ($img.Height -eq 256) { 0 } else { [byte]$img.Height }
        $ms.WriteByte($w)               # width  (0 = 256)
        $ms.WriteByte($h)               # height (0 = 256)
        $ms.WriteByte(0)                # color count (0 = true color)
        $ms.WriteByte(0)                # reserved
        $ms.Write([BitConverter]::GetBytes([uint16]1), 0, 2)  # color planes
        $ms.Write([BitConverter]::GetBytes([uint16]32), 0, 2) # bits per pixel
        $ms.Write([BitConverter]::GetBytes([uint32]$img.Bytes.Length), 0, 4)
        $ms.Write([BitConverter]::GetBytes([uint32]$offset), 0, 4)
        $offset += $img.Bytes.Length
    }

    # Write PNG data
    foreach ($img in $images) {
        $ms.Write($img.Bytes, 0, $img.Bytes.Length)
    }

    [IO.File]::WriteAllBytes($Destination, $ms.ToArray())
    $ms.Dispose()
    $len = $ms.Length; Write-Host "[ICO] Written $Destination ($([math]::Round($len/1024,1)) KB)"
}

# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
# Main
# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Write-Host '=== VYDRIX icon generator ==='

$usedInkscape = $false
if (Test-Path $svgPath) {
    $usedInkscape = Try-Inkscape
}

if (-not $usedInkscape) {
    Write-Host '[GDI+] Inkscape not found or SVG missing â€” using System.Drawing fallback'
    Make-Gdi
}

# Collect all PNGs in order
$pngFiles = $sizes | ForEach-Object {
    $p = Join-Path $outDir "vydrix_${_}.png"
    if (Test-Path $p) { $p }
} | Where-Object { $_ }

if ($pngFiles.Count -eq 0) {
    Write-Error 'No PNG files were produced â€” cannot build ICO.'
}

Build-Ico -PngPaths $pngFiles -Destination $icoPath

Write-Host ''
Write-Host '=== Files created ==='
$allFiles = @($icoPath) + $pngFiles
foreach ($f in $allFiles) {
    if (Test-Path $f) {
        $info = Get-Item $f
        Write-Host ("  {0,-40} {1,8} bytes" -f $info.Name, $info.Length)
    }
}
Write-Host '=== Done ==='

