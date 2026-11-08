<#
.SYNOPSIS
  Regenerates every icon the app uses from the master logo in branding\icon.jpg.
.DESCRIPTION
  Windows PowerShell / PowerShell 7 on Windows (uses System.Drawing). Writes:
    src\InventoryTracker\wwwroot\favicon.ico             16/32/48/256 px, also the executable's icon (ApplicationIcon)
    src\InventoryTracker\wwwroot\img\apple-touch-icon.png  180 px tile
    src\InventoryTracker\wwwroot\img\icon-192.png          192 px tile
    src\InventoryTracker\wwwroot\img\logo.png              tightly cropped, transparent background (sidebar brand)
  The "tile" is the artwork cropped to its content, centred on a white square with a margin, so it reads on both
  light and dark browser tabs. Run it again after replacing branding\icon.jpg, then rebuild.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$WebRoot,
    # Share of the tile left empty around the artwork (each side).
    [double]$Margin = 0.10,
    # Pixels with every channel above this are treated as background when finding the artwork.
    [int]$BackgroundThreshold = 245
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Join-Path $here '..\branding\icon.jpg' }
if (-not $WebRoot) { $WebRoot = Join-Path $here '..\src\InventoryTracker\wwwroot' }
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class IconTools
{
    // Smallest rectangle containing every pixel that is not (near-)white.
    public static Rectangle ContentBounds(Bitmap bmp, int threshold)
    {
        int w = bmp.Width, h = bmp.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[Math.Abs(data.Stride) * h];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * data.Stride + x * 4;
                    if (buffer[i] < threshold || buffer[i + 1] < threshold || buffer[i + 2] < threshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        if (maxX < 0) throw new InvalidOperationException("The source image has no artwork (it is all white).");
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // Turns "artwork on white" into "artwork on transparent": alpha from how far each pixel is from white, colour
    // un-mixed from the white background, so anti-aliased edges have no white halo on any background.
    public static Bitmap WhiteToAlpha(Bitmap source)
    {
        int w = source.Width, h = source.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var src = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dst = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var inBuf = new byte[Math.Abs(src.Stride) * h];
            var outBuf = new byte[Math.Abs(dst.Stride) * h];
            Marshal.Copy(src.Scan0, inBuf, 0, inBuf.Length);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * src.Stride + x * 4, o = y * dst.Stride + x * 4;
                    double b = inBuf[i], g = inBuf[i + 1], r = inBuf[i + 2];
                    double a = 1.0 - Math.Min(r, Math.Min(g, b)) / 255.0;
                    if (a < 0.004) { continue; } // stays fully transparent
                    outBuf[o]     = (byte)Math.Max(0, Math.Min(255, (b - 255.0 * (1 - a)) / a));
                    outBuf[o + 1] = (byte)Math.Max(0, Math.Min(255, (g - 255.0 * (1 - a)) / a));
                    outBuf[o + 2] = (byte)Math.Max(0, Math.Min(255, (r - 255.0 * (1 - a)) / a));
                    outBuf[o + 3] = (byte)Math.Round(a * 255.0);
                }
            }
            Marshal.Copy(outBuf, 0, dst.Scan0, outBuf.Length);
        }
        finally { source.UnlockBits(src); result.UnlockBits(dst); }
        return result;
    }
}
'@ -ReferencedAssemblies System.Drawing

function New-Resized([System.Drawing.Image]$image, [int]$width, [int]$height) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.DrawImage($image, 0, 0, $width, $height)
    }
    finally { $g.Dispose() }
    return $bmp
}

function Save-Png([System.Drawing.Image]$image, [string]$path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $image.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Get-PngBytes([System.Drawing.Image]$image) {
    $stream = New-Object System.IO.MemoryStream
    try { $image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); return , $stream.ToArray() }
    finally { $stream.Dispose() }
}

$sourcePath = (Resolve-Path $Source).Path
$webRootPath = (Resolve-Path $WebRoot).Path
$original = New-Object System.Drawing.Bitmap $sourcePath
try {
    $box = [IconTools]::ContentBounds($original, $BackgroundThreshold)
    Write-Host ("Artwork {0}x{1} at ({2},{3}) inside {4}x{5}" -f $box.Width, $box.Height, $box.X, $box.Y, $original.Width, $original.Height)

    # --- Square tile: artwork centred on white, with a margin -------------------------------------------------
    $side = [int][Math]::Ceiling([Math]::Max($box.Width, $box.Height) / (1 - 2 * $Margin))
    $tile = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($tile)
    try {
        $g.Clear([System.Drawing.Color]::White)
        $g.DrawImage($original, [int](($side - $box.Width) / 2), [int](($side - $box.Height) / 2), $box, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $g.Dispose() }

    # --- favicon.ico: PNG-compressed images, 16/32/48/256 ------------------------------------------------------
    $sizes = 16, 32, 48, 256
    $images = foreach ($size in $sizes) {
        $resized = New-Resized $tile $size $size
        try { , (Get-PngBytes $resized) } finally { $resized.Dispose() }
    }
    $icoPath = Join-Path $webRootPath 'favicon.ico'
    $ico = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $ico
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($png in $images) { $writer.Write([byte[]]$png) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
    $writer.Dispose()
    Write-Host "favicon.ico  ($($sizes -join '/'))"

    # --- PNG tiles --------------------------------------------------------------------------------------------
    foreach ($spec in @(@{ Name = 'apple-touch-icon.png'; Size = 180 }, @{ Name = 'icon-192.png'; Size = 192 })) {
        $resized = New-Resized $tile $spec.Size $spec.Size
        try { Save-Png $resized (Join-Path $webRootPath "img\$($spec.Name)") } finally { $resized.Dispose() }
        Write-Host "img\$($spec.Name)"
    }

    # --- Transparent logo for the sidebar, cropped tight, 2x for a ~2.5rem wide mark -----------------------
    $cropped = $original.Clone($box, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $transparent = [IconTools]::WhiteToAlpha($cropped)
    $logoWidth = 160
    $logoHeight = [int][Math]::Round($logoWidth * $box.Height / $box.Width)
    $logo = New-Resized $transparent $logoWidth $logoHeight
    try { Save-Png $logo (Join-Path $webRootPath 'img\logo.png') } finally { $logo.Dispose(); $transparent.Dispose(); $cropped.Dispose() }
    Write-Host "img\logo.png ($logoWidth x $logoHeight)"

    $tile.Dispose()
}
finally { $original.Dispose() }
