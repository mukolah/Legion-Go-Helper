# Generates app.ico: the tray/window icon shape (ring + centre dot, see TrayIcon.Make in MainForm.cs)
# split into four quadrants, one per performance mode colour (Theme in UI/Controls.cs).
# Clockwise from top-left: Quiet (green), Balanced (blue), Performance (red), Custom (orange).
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File app\Assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$colors = @(
    [System.Drawing.Color]::FromArgb(6, 180, 138),   # Quiet       - top-left
    [System.Drawing.Color]::FromArgb(58, 174, 239),  # Balanced    - top-right
    [System.Drawing.Color]::FromArgb(255, 32, 32),   # Performance - bottom-right
    [System.Drawing.Color]::FromArgb(255, 128, 0)    # Custom      - bottom-left
)
$sizes = 16, 24, 32, 48, 64, 128, 256

function Render([int]$size) {
    $s = $size / 32.0
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $h = $size / 2.0
    $quads = @(
        (New-Object System.Drawing.RectangleF(0, 0, $h, $h)),
        (New-Object System.Drawing.RectangleF($h, 0, $h, $h)),
        (New-Object System.Drawing.RectangleF($h, $h, $h, $h)),
        (New-Object System.Drawing.RectangleF(0, $h, $h, $h))
    )
    for ($i = 0; $i -lt 4; $i++) {
        $g.SetClip($quads[$i])
        $pen = New-Object System.Drawing.Pen($colors[$i], [single](5 * $s))
        $g.DrawEllipse($pen, [single](4 * $s), [single](4 * $s), [single](23 * $s), [single](23 * $s))
        $brush = New-Object System.Drawing.SolidBrush($colors[$i])
        $g.FillEllipse($brush, [single](11 * $s), [single](11 * $s), [single](10 * $s), [single](10 * $s))
        $pen.Dispose(); $brush.Dispose()
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large entry as PNG (standard for 256px icons)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small entries as classic 32bpp DIB: header (height doubled), bottom-up BGRA, empty AND mask
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $maskStride = [int][math]::Ceiling($size / 32.0) * 4
        $bw.Write([uint32]($size * $size * 4 + $maskStride * $size))
        $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $p = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

# ICO container: DIB entries up to 128px, PNG for 256px
$images = @($sizes | ForEach-Object { , (Render $_) })
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
$path = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
Write-Host "Wrote $path ($($out.Length) bytes)"
