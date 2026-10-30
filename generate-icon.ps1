# generate-icon.ps1 - Draw and save dsh-panel.ico (multi-size, for /win32icon)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File generate-icon.ps1
# ASCII only (no BOM needed). Re-run any time to regenerate the icon.
#
# Design (v3.7): one blue rounded square + a white prompt mark ">_" - the same mark the
# panel header, the tray badge and the workbench use. Every shape is round-capped or
# round-joined on purpose: no sharp corners, so it survives down to 16 px.

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

function New-RoundRect([System.Drawing.Rectangle]$rect, [int]$radius) {
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 1) rounded square: ~0.30 radius (noticeably softer than a plain app tile)
    $inset = [Math]::Max(1, [int]($size * 0.03))
    $radius = [Math]::Max(3, [int][Math]::Round($size * 0.30))
    $rect = New-Object System.Drawing.Rectangle -ArgumentList $inset, $inset, ($size - $inset * 2), ($size - $inset * 2)
    $path = New-RoundRect $rect $radius

    if ($size -ge 32) {
        $c1 = [System.Drawing.Color]::FromArgb(92, 143, 255)    # #5C8FFF light end
        $c2 = [System.Drawing.Color]::FromArgb(21, 69, 198)     # #1545C6 deep end
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $rect, $c1, $c2, 62.0
    } else {
        # tiny sizes: flat mid blue reads cleaner than a gradient at 16 px
        $brush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(47, 107, 255))
    }
    $g.FillPath($brush, $path)
    $brush.Dispose()

    # 2) soft top highlight, clipped to the tile
    if ($size -ge 32) {
        $g.SetClip($path)
        $hiH = [int]($rect.Height * 0.52)
        $hiRect = New-Object System.Drawing.Rectangle -ArgumentList $rect.X, $rect.Y, $rect.Width, $hiH
        $hi = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(34, 255, 255, 255))
        $g.FillRectangle($hi, $hiRect)
        $hi.Dispose()
        $g.ResetClip()
    }
    $path.Dispose()

    # 3) white ">_" prompt, round caps only
    $u = $size / 26.0
    $cx = $size / 2.0
    $cy = $size / 2.0
    $w = [Math]::Max(1.5, 2.5 * $u)
    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::White), ([single]$w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $chevron = [System.Drawing.PointF[]]@(
        ([System.Drawing.PointF]::new([single]($cx - 6.4 * $u), [single]($cy - 4.8 * $u))),
        ([System.Drawing.PointF]::new([single]($cx - 1.6 * $u), [single]($cy))),
        ([System.Drawing.PointF]::new([single]($cx - 6.4 * $u), [single]($cy + 4.8 * $u)))
    )
    $g.DrawLines($pen, $chevron)
    $g.DrawLine($pen,
        [single]($cx + 0.4 * $u), [single]($cy + 4.8 * $u),
        [single]($cx + 6.2 * $u), [single]($cy + 4.8 * $u))
    $pen.Dispose()

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
