# Generates the app icon files from the source art in branding/emmaspace-icon.png:
#   src/EmmaSpace.App/Assets/emmaspace.ico  (16-256 px, PNG-compressed entries)
#   src/EmmaSpace.App/Assets/emmaspace.png  (256 px, used for the title bar logo)
# Corners are rounded like modern app icons. Windows only (System.Drawing).
# Sizes up to 24 px use a tighter crop around the face, so the tiny icons stay recognisable.
# Usage: pwsh build/make-icon.ps1 [-Source <png>] [-CornerRadius 0.2] [-SmallCrop 0.24,0.14,0.62]
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\branding\emmaspace-icon.png'),
    [double] $CornerRadius = 0.2,
    # x, y and side of the small-size crop, as fractions of the source width
    [double[]] $SmallCrop = @(0.24, 0.14, 0.62)
)
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\EmmaSpace.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$art = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

function New-RoundedRect([float]$size, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    if ($r -le 0) { $p.AddRectangle([System.Drawing.RectangleF]::new(0, 0, $size, $size)); return $p }
    $d = $r * 2
    $p.AddArc(0, 0, $d, $d, 180, 90)
    $p.AddArc($size - $d, 0, $d, $d, 270, 90)
    $p.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $p.AddArc(0, $size - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $src = if ($size -le 24) {
        [System.Drawing.Rectangle]::new([int]($art.Width * $SmallCrop[0]), [int]($art.Width * $SmallCrop[1]),
                                        [int]($art.Width * $SmallCrop[2]), [int]($art.Width * $SmallCrop[2]))
    } else {
        [System.Drawing.Rectangle]::new(0, 0, $art.Width, $art.Height)
    }
    # Downscale with high-quality bicubic sampling, then clip to a rounded square with anti-aliasing.
    $scaled = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($scaled)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'HighQuality'
    $attrs = [System.Drawing.Imaging.ImageAttributes]::new()
    $attrs.SetWrapMode('TileFlipXY') # avoids dark fringes at the edges
    $g.DrawImage($art, [System.Drawing.Rectangle]::new(0, 0, $size, $size), $src.X, $src.Y, $src.Width, $src.Height, 'Pixel', $attrs)
    $g.Dispose()

    $out = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = [System.Drawing.TextureBrush]::new($scaled)
    $g.FillPath($brush, (New-RoundedRect $size ($size * $CornerRadius)))
    $g.Dispose()
    $scaled.Dispose()
    return $out
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -eq 256) { $bmp.Save((Join-Path $assets 'emmaspace.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    , (ConvertTo-PngBytes $bmp)
    $bmp.Dispose()
}
$art.Dispose()

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'emmaspace.ico'), $out.ToArray())
Write-Host "Wrote $(Join-Path $assets 'emmaspace.ico') and emmaspace.png"
