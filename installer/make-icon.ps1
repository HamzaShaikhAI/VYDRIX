# Generates Assets\vydrix.ico (16-256 px PNG frames) for the VYDRIX desktop app.
param([string]$Out = "$PSScriptRoot\..\src\Vydrix.Desktop\Assets\vydrix.ico")
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [Math]::Max(2, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($s - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($s - $r - 1, $s - $r - 1, $r, $r, 0, 90); $path.AddArc(0, $s - $r - 1, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 18, 21, 27)), $path)
    $g.DrawPath([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 59, 130, 246), [single][Math]::Max(1, $s / 32)), $path)
    # cloud arc
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 59, 130, 246), [single][Math]::Max(1.5, $s / 14))
    $g.DrawArc($pen, [single]($s * 0.16), [single]($s * 0.20), [single]($s * 0.40), [single]($s * 0.40), 150, 200)
    $g.DrawArc($pen, [single]($s * 0.40), [single]($s * 0.12), [single]($s * 0.44), [single]($s * 0.44), 190, 190)
    # X
    $xp = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 139, 92, 246), [single][Math]::Max(2, $s / 9))
    $xp.StartCap = 'Round'; $xp.EndCap = 'Round'
    $g.DrawLine($xp, [single]($s * 0.36), [single]($s * 0.52), [single]($s * 0.66), [single]($s * 0.82))
    $g.DrawLine($xp, [single]($s * 0.66), [single]($s * 0.52), [single]($s * 0.36), [single]($s * 0.82))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { ,(New-Frame $_) }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset); $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close()
Write-Host "Wrote $Out"
