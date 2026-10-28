# PowerShell 7 (pwsh). Windows only (System.Drawing).
#Requires -Version 7.0

# =====================================================================
# Makes the Android images from the project's app icon (assets/app_icon.png), so that no
# third-party art is added:
#   1. The launcher icon of the app, per density (src/PdfViewer.Android/Resources/mipmap-*).
#   2. The store icon, 512 x 512 (fastlane/metadata/android/en-US/images/icon.png).
#   3. The feature graphic, 1024 x 500, 24-bit RGB (fastlane/metadata/android/en-US/images/featureGraphic.png).
#   4. The phone screenshots in images/phoneScreenshots, flattened to 24-bit RGB.
# Google Play and F-Droid both read the fastlane folder (docs/android-release.md,
# docs/android-fdroid.md). scripts/convert_icons.ps1 renders assets/app_icon.png from the SVG
# master (original project art) and runs this script; the outputs are committed.
# =====================================================================

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$RootDir = Split-Path -Parent $PSScriptRoot
$Images = Join-Path $RootDir 'fastlane\metadata\android\en-US\images'
New-Item -ItemType Directory -Path $Images -Force | Out-Null

function New-Canvas([int]$width, [int]$height,
        [System.Drawing.Imaging.PixelFormat]$format = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, $format)
    $bitmap.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $bitmap, $g
}

function Draw-Icon($g, [System.Drawing.Image]$icon, [int]$x, [int]$y, [int]$side) {
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # no grey edge
    $g.DrawImage($icon, [System.Drawing.Rectangle]::new($x, $y, $side, $side), 0, 0, $icon.Width, $icon.Height,
        [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    $attributes.Dispose()
}

$Icon = [System.Drawing.Image]::FromFile((Join-Path $RootDir 'assets\app_icon.png'))
try {
    # 1. Launcher icons (legacy square icons with a transparent background; Android 8+ shows
    #    them on its own plate).
    $Densities = @{ 'mdpi' = 48; 'hdpi' = 72; 'xhdpi' = 96; 'xxhdpi' = 144; 'xxxhdpi' = 192 }
    foreach ($d in $Densities.GetEnumerator()) {
        $dir = Join-Path $RootDir "src\PdfViewer.Android\Resources\mipmap-$($d.Key)"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $bitmap, $g = New-Canvas $d.Value $d.Value
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            Draw-Icon $g $Icon 0 0 $d.Value
        } finally { $g.Dispose() }
        $bitmap.Save((Join-Path $dir 'ic_launcher.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }

    # 2. Store icon: a full square (the stores round the corners themselves), so the icon's red
    #    tile is drawn edge to edge on its own colour, which fills the tile's rounded corners.
    $bitmap, $g = New-Canvas 512 512
    try {
        $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#C8102E'))
        Draw-Icon $g $Icon 0 0 512
    } finally { $g.Dispose() }
    $bitmap.Save((Join-Path $Images 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    # 3. Feature graphic: the dark colour of the icon's pen, the icon on the left, the name and
    #    two lines.
    #    Play takes a JPEG or a 24-bit PNG without alpha here (only the 512 icon may be 32-bit).
    $bitmap, $g = New-Canvas 1024 500 ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    try {
        $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#1F2328'))
        Draw-Icon $g $Icon 100 110 280
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $soft = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F4C3CB'))
        $title = [System.Drawing.Font]::new('Segoe UI Semibold', 64, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $line = [System.Drawing.Font]::new('Segoe UI', 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString('PDF Viewer', $title, $white, 440, 160)
        $g.DrawString('Read, fill and annotate PDFs.', $line, $soft, 444, 255)
        $g.DrawString('No permissions. No tracking.', $line, $soft, 444, 300)
        $title.Dispose(); $line.Dispose(); $white.Dispose(); $soft.Dispose()
    } finally { $g.Dispose() }
    $bitmap.Save((Join-Path $Images 'featureGraphic.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
} finally { $Icon.Dispose() }

# 4. Phone screenshots (taken with `adb exec-out screencap -p`, which writes RGBA): flattened on
#    white into 24-bit RGB, because Play refuses screenshots with an alpha channel.
foreach ($file in Get-ChildItem (Join-Path $Images 'phoneScreenshots') -Filter *.png -ErrorAction SilentlyContinue) {
    $source = [System.Drawing.Image]::FromFile($file.FullName)
    try {
        $bitmap, $g = New-Canvas $source.Width $source.Height ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $g.Clear([System.Drawing.Color]::White)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $g.DrawImage($source, 0, 0, $source.Width, $source.Height)
        } finally { $g.Dispose() }
    } finally { $source.Dispose() }
    $bitmap.Save($file.FullName, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# PNG colour type in the IHDR chunk (byte 25): 2 = RGB, 6 = RGBA. Play needs 2 for the feature
# graphic and the screenshots.
$rgb = @(Join-Path $Images 'featureGraphic.png') + @(Get-ChildItem (Join-Path $Images 'phoneScreenshots') -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($path in $rgb) {
    $type = [System.IO.File]::ReadAllBytes($path)[25]
    if ($type -ne 2) { throw "$path has PNG colour type $type, not 2 (RGB)" }
}

Write-Host "Android images written."
