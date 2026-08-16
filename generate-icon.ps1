# generate-icon.ps1 - Draw and save dsh-panel.ico (multi-size, for /win32icon)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File generate-icon.ps1
# ASCII only (no BOM needed). Re-run any time to regenerate the icon.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot 'dsh-panel.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class IconHelper {
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
"@

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square
    $radius = [Math]::Max(2, [int]($size * 0.22))
    $rect = New-Object System.Drawing.Rectangle -ArgumentList 1, 1, ($size - 2), ($size - 2)
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    if ($size -ge 32) {
        $c1 = [System.Drawing.Color]::FromArgb(79, 70, 229)   # indigo
        $c2 = [System.Drawing.Color]::FromArgb(124, 58, 237)  # violet
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $rect, $c1, $c2, 45.0
        $g.FillPath($brush, $path)
        $brush.Dispose()
    } else {
        $c3 = [System.Drawing.Color]::FromArgb(99, 91, 255)
        $brush = New-Object System.Drawing.SolidBrush -ArgumentList $c3
        $g.FillPath($brush, $path)
        $brush.Dispose()
    }
    $path.Dispose()

    # white bold letter D
    $fontSize = $size * 0.58
    $font = New-Object System.Drawing.Font -ArgumentList 'Segoe UI', $fontSize,
        ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::White)
    $rectF = New-Object System.Drawing.RectangleF -ArgumentList 0, 0, $size, $size
    $g.DrawString('D', $font, $textBrush, $rectF, $sf)
    $textBrush.Dispose()
    $sf.Dispose()
    $font.Dispose()
    $g.Dispose()

    # serialize as a single-size ico, return raw bytes
    $hIcon = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $ms = New-Object System.IO.MemoryStream
    $icon.Save($ms)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $icon.Dispose()
    [IconHelper]::DestroyIcon($hIcon) | Out-Null
    $bmp.Dispose()
    return ,$bytes
}

$images = @()
foreach ($s in $sizes) {
    $data = Draw-Icon $s
    # single-size ico layout: 6-byte header + 16-byte entry + image data
    [byte[]]$imageData = $data[22..($data.Length - 1)]
    $images += ,@{ Size = $s; Data = $imageData }
}

# merge into a multi-size ico
$fs = New-Object System.IO.FileStream -ArgumentList $out, ([System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$images.Count)     # count
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img.Size
    $w = 0
    if ($s -lt 256) { $w = $s }
    $bw.Write([Byte]$w)              # width (0 = 256)
    $bw.Write([Byte]$w)              # height (0 = 256)
    $bw.Write([Byte]0)               # color count
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # planes
    $bw.Write([UInt16]32)            # bit count
    $bw.Write([UInt32]$img.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) {
    $bw.Write($img.Data)
}
$bw.Flush()
$bw.Close()
$fs.Close()

$len = (Get-Item $out).Length
Write-Output ("saved: " + $out + " (" + $len + " bytes, " + $images.Count + " sizes)")
