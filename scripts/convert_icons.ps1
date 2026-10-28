# PowerShell 7 (pwsh). Windows only (System.Drawing).
#Requires -Version 7.0

# =====================================================================
# Makes every icon file of the project from the SVG masters in assets/:
#   app_icon.svg        the app icon, 32 px and up
#   app_icon_small.svg  the app icon at 16 and 24 px (no pen: it turns to noise there)
#   pdf_file.svg        the .pdf file-type icon, 32 px and up
#   pdf_file_small.svg  the file-type icon at 16 and 24 px (no letters)
# The masters are original project art (assets/app_icon.brief.md). Outputs, all committed:
#   1. assets/app_icon.png, assets/pdf_file.png (512 x 512) and assets/app_icon.ico,
#      assets/pdf_file.ico (16, 24, 32, 48, 64 and 256 px, each frame drawn from its SVG at that
#      size), copied to src/PdfViewer/assets and src/Installer/assets (embedded resources).
#   2. docs/assets/img/app-icon.png (site logo and favicon) and src/PdfViewer.Web/wwwroot/favicon.svg.
#   3. The Android launcher icons and Play/F-Droid images (scripts/android_store_images.ps1).
# The MSIX logos are made from these files when the package is built (scripts/build_msix.ps1);
# eng/apidocs/docfx.json takes assets/app_icon.png as it is.
#
# The SVGs are drawn by headless Chrome or Edge (no extra package): the browser renders one page
# with every image at its pixel size, and System.Drawing cuts the images out of the screenshot.
# Set ICON_BROWSER to the browser's path when it is not in the usual place.
#
#   pwsh scripts/convert_icons.ps1                       # regenerate everything
#   pwsh scripts/convert_icons.ps1 -Sheet out\icons.png  # also a light/dark check sheet
# =====================================================================

param(
    # Where to write a verification sheet (light and dark backgrounds, 256/48/32/16 px, the
    # Android launcher icon in a circle mask). Nothing is written when it is empty.
    [string]$Sheet = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$RootDir = Split-Path -Parent $PSScriptRoot
$AssetsDir = Join-Path $RootDir 'assets'
$IcoSizes = @(16, 24, 32, 48, 64, 256)
$SmallLimit = 24 # sizes up to this use the *_small.svg variant

function Find-Browser {
    $candidates = @(
        $env:ICON_BROWSER,
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    throw 'No Chrome or Edge found. Set ICON_BROWSER to the path of chrome.exe or msedge.exe.'
}

# Screenshots an HTML page of $width x $height CSS pixels at device scale 1, with a transparent
# background, and returns it as a System.Drawing.Bitmap.
function Get-PageShot([string]$html, [int]$width, [int]$height) {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfviewer-icons-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $page = Join-Path $work 'page.html'
        $shot = Join-Path $work 'shot.png'
        Set-Content -Path $page -Value $html -Encoding utf8NoBOM
        $arguments = @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            '--no-default-browser-check', '--force-device-scale-factor=1',
            '--default-background-color=00000000', "--user-data-dir=$work\profile",
            "--window-size=$width,$height", '--virtual-time-budget=3000',
            "--screenshot=$shot", ([System.Uri]$page).AbsoluteUri
        )
        $process = Start-Process -FilePath (Find-Browser) -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'The browser did not finish the screenshot in 60 s.' }
        if (-not (Test-Path $shot)) { throw "The browser wrote no screenshot (exit code $($process.ExitCode))." }
        $bytes = [System.IO.File]::ReadAllBytes($shot)
        $bitmap = [System.Drawing.Bitmap]::new([System.IO.MemoryStream]::new($bytes))
        if ($bitmap.Width -ne $width -or $bitmap.Height -ne $height) {
            throw "The screenshot is $($bitmap.Width) x $($bitmap.Height), not $width x $height."
        }
        return $bitmap
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

# Renders each job (@{ Svg = path; Size = px }) at its size, on one page, and returns the PNG
# bytes of each, in order.
function Convert-SvgsToPng([object[]]$jobs) {
    $pageWidth = 1200
    $x = 0; $y = 0; $row = 0
    $images = [System.Text.StringBuilder]::new()
    foreach ($job in $jobs) {
        if ($x + $job.Size -gt $pageWidth) { $x = 0; $y += $row + 8; $row = 0 }
        $job.X = $x; $job.Y = $y
        $data = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($job.Svg))
        [void]$images.Append("<img src=`"data:image/svg+xml;base64,$data`" style=`"left:${x}px;top:${y}px;width:$($job.Size)px;height:$($job.Size)px`">")
        $x += $job.Size + 8
        $row = [Math]::Max($row, $job.Size)
    }
    $pageHeight = $y + $row
    $html = "<!doctype html><html><head><meta charset=`"utf-8`"><style>html,body{margin:0;background:transparent}img{position:absolute;display:block}</style></head><body>$images</body></html>"
    $shot = Get-PageShot $html $pageWidth $pageHeight
    try {
        foreach ($job in $jobs) {
            $crop = $shot.Clone([System.Drawing.Rectangle]::new($job.X, $job.Y, $job.Size, $job.Size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $ms = [System.IO.MemoryStream]::new()
            $crop.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $crop.Dispose()
            , $ms.ToArray() # the comma keeps the array whole in the pipeline
            $ms.Dispose()
        }
    } finally { $shot.Dispose() }
}

# Writes an .ico of PNG frames (Windows Vista and later read PNG frames of any size).
function Write-Ico([string]$path, [int[]]$sizes, [System.Collections.Generic.List[byte[]]]$frames) {
    $fs = [System.IO.FileStream]::new($path, [System.IO.FileMode]::Create)
    $writer = [System.IO.BinaryWriter]::new($fs)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $side = if ($sizes[$i] -ge 256) { [byte]0 } else { [byte]$sizes[$i] }
            $writer.Write($side); $writer.Write($side)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose(); $fs.Dispose() }
}

# 1. PNG masters and ICOs.
$Icons = @(
    @{ Name = 'app_icon'; Large = Join-Path $AssetsDir 'app_icon.svg'; Small = Join-Path $AssetsDir 'app_icon_small.svg' },
    @{ Name = 'pdf_file'; Large = Join-Path $AssetsDir 'pdf_file.svg'; Small = Join-Path $AssetsDir 'pdf_file_small.svg' }
)
$Jobs = @()
foreach ($icon in $Icons) {
    $Jobs += [pscustomobject]@{ Icon = $icon.Name; Svg = $icon.Large; Size = 512; X = 0; Y = 0 }
    foreach ($size in $IcoSizes) {
        $svg = if ($size -le $SmallLimit) { $icon.Small } else { $icon.Large }
        $Jobs += [pscustomobject]@{ Icon = $icon.Name; Svg = $svg; Size = $size; X = 0; Y = 0 }
    }
}
Write-Host "Rendering $($Jobs.Count) images from the SVG masters..."
$Pngs = @(Convert-SvgsToPng $Jobs)
foreach ($icon in $Icons) {
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    for ($i = 0; $i -lt $Jobs.Count; $i++) {
        if ($Jobs[$i].Icon -ne $icon.Name) { continue }
        if ($Jobs[$i].Size -eq 512) { [System.IO.File]::WriteAllBytes((Join-Path $AssetsDir "$($icon.Name).png"), $Pngs[$i]) }
        else { $frames.Add($Pngs[$i]) }
    }
    Write-Ico (Join-Path $AssetsDir "$($icon.Name).ico") $IcoSizes $frames
    Write-Host "   - assets/$($icon.Name).png, assets/$($icon.Name).ico ($((Get-Item (Join-Path $AssetsDir "$($icon.Name).ico")).Length) bytes)"
}

# The desktop app and the installer embed these four files.
$Runtime = 'app_icon.ico', 'app_icon.png', 'pdf_file.ico', 'pdf_file.png'
foreach ($target in 'src\PdfViewer\assets', 'src\Installer\assets') {
    foreach ($file in $Runtime) { Copy-Item (Join-Path $AssetsDir $file) (Join-Path $RootDir "$target\$file") -Force }
}

# 2. Site logo and favicons.
Copy-Item (Join-Path $AssetsDir 'app_icon.png') (Join-Path $RootDir 'docs\assets\img\app-icon.png') -Force
Copy-Item (Join-Path $AssetsDir 'app_icon_small.svg') (Join-Path $RootDir 'src\PdfViewer.Web\wwwroot\favicon.svg') -Force

# 3. Android launcher icons and store images.
& (Join-Path $PSScriptRoot 'android_store_images.ps1')

# 4. Verification sheet (not committed).
if ($Sheet) {
    function Get-DataUri([string]$path) { 'data:image/png;base64,' + [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($path)) }
    function Get-IcoFrame([string]$path, [int]$size) {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $count = [BitConverter]::ToUInt16($bytes, 4)
        for ($i = 0; $i -lt $count; $i++) {
            $entry = 6 + 16 * $i
            $side = if ($bytes[$entry] -eq 0) { 256 } else { $bytes[$entry] }
            if ($side -eq $size) {
                $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
                $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
                return 'data:image/png;base64,' + [Convert]::ToBase64String($bytes, $offset, $length)
            }
        }
        throw "$path has no $size px frame."
    }
    $launcher = Get-DataUri (Join-Path $RootDir 'src\PdfViewer.Android\Resources\mipmap-xxxhdpi\ic_launcher.png')
    $launcherSmall = Get-DataUri (Join-Path $RootDir 'src\PdfViewer.Android\Resources\mipmap-mdpi\ic_launcher.png')
    $panels = foreach ($theme in @(@{ Name = 'Light'; Bg = '#ffffff'; Fg = '#1f2328' }, @{ Name = 'Dark'; Bg = '#202020'; Fg = '#e6edf3' })) {
        $rows = foreach ($name in 'app_icon', 'pdf_file') {
            $ico = Join-Path $AssetsDir "$name.ico"
            $cells = foreach ($size in 256, 48, 32, 16) { "<figure><img src=`"$(Get-IcoFrame $ico $size)`" width=`"$size`" height=`"$size`"><figcaption>$size</figcaption></figure>" }
            $zoom = foreach ($size in 32, 24, 16) { "<figure><img class=`"px`" src=`"$(Get-IcoFrame $ico $size)`" width=`"$($size * 6)`" height=`"$($size * 6)`"><figcaption>$size x6</figcaption></figure>" }
            "<div class=`"row`"><h2>$name.ico</h2>$($cells -join '')$($zoom -join '')</div>"
        }
        $android = "<div class=`"row`"><h2>Android launcher (circle mask)</h2><figure><div class=`"circle`" style=`"width:192px;height:192px`"><img src=`"$launcher`" width=`"192`" height=`"192`"></div><figcaption>xxxhdpi 192</figcaption></figure><figure><div class=`"circle`" style=`"width:48px;height:48px`"><img src=`"$launcherSmall`" width=`"48`" height=`"48`"></div><figcaption>mdpi 48</figcaption></figure></div>"
        "<section style=`"background:$($theme.Bg);color:$($theme.Fg)`"><h1>$($theme.Name)</h1>$($rows -join '')$android</section>"
    }
    $html = "<!doctype html><html><head><meta charset=`"utf-8`"><style>body{margin:0;font:13px 'Segoe UI',sans-serif}section{padding:16px 24px;height:1032px;box-sizing:border-box;float:left;width:1000px}h1{font-size:18px;margin:0 0 8px}h2{font-size:13px;margin:0 0 6px;width:100%}.row{display:flex;flex-wrap:wrap;align-items:flex-end;gap:20px;margin-bottom:18px}figure{margin:0;text-align:center}figcaption{opacity:.7;margin-top:4px}img{display:block}.px{image-rendering:pixelated}.circle{border-radius:50%;overflow:hidden}</style></head><body>$($panels -join '')</body></html>"
    $shot = Get-PageShot $html 2000 1032
    New-Item -ItemType Directory -Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($Sheet))) -Force | Out-Null
    $shot.Save([System.IO.Path]::GetFullPath($Sheet), [System.Drawing.Imaging.ImageFormat]::Png)
    $shot.Dispose()
    Write-Host "Verification sheet: $Sheet"
}

Write-Host 'All icons regenerated.' -ForegroundColor Green
