# PowerShell 7 (pwsh). Windows only: it needs the Windows SDK (makeappx.exe, makepri.exe).
#Requires -Version 7.0

# =====================================================================
# Builds the Microsoft Store package (docs/microsoft-store.md).
# Produces:
#   - publish/msix/PdfViewer-<version>-x64.msix  (unsigned: the Store signs it on submission)
#   - publish/msix/layout/                       (the unpacked package, for a local test install)
#
# The package holds a self-contained, unbundled win-x64 publish of the same application that
# scripts/build_publish.ps1 ships:
#   - Self-contained: a packaged app cannot rely on a .NET runtime installed on the machine.
#   - Unbundled: the package is already compressed and block-mapped, so the Store sends only the
#     changed blocks of each file on update. A single-file exe would change as a whole on every
#     release, and would extract its native parts to %TEMP% on start.
#   - Read Aloud (System.Speech.dll) ships inside the package: the package folder is read-only,
#     so the application cannot download the component beside itself.
# =====================================================================

param(
    # The four Partner Center values (Product management > Product identity). Parameters win over
    # the identity file.
    [string]$IdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [string]$DisplayName,
    [string]$IdentityFile,

    # Builds with a local test identity when no real one is set. Such a package cannot be submitted
    # to the Store; it is for packaging checks (CI before registration) and local test installs.
    [switch]$TestIdentity,

    # Four-part package version. Default: the nearest v* git tag, with revision 0 (the Store
    # requires revision 0).
    [string]$Version,

    # Optional, for local tests only: sign the .msix with this PFX. The Store does not need it.
    [string]$CertificatePfx,
    [string]$CertificatePassword,

    # Use an existing publish folder instead of running dotnet publish.
    [string]$AppDir,

    [string]$OutputDir,

    # Windows SDK bin folder (holds makeappx.exe, makepri.exe, signtool.exe). Default: the newest
    # installed x64 one.
    [string]$SdkBinDir,

    # Skip the PDFium-free gate (it launches the application once).
    [switch]$NoGate
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
Set-Location $RootDir

if ([string]::IsNullOrEmpty($IdentityFile)) { $IdentityFile = Join-Path $RootDir "eng\msix\identity.json" }
if ([string]::IsNullOrEmpty($OutputDir)) { $OutputDir = Join-Path $RootDir "publish\msix" }
$Layout = Join-Path $OutputDir "layout"

# ---------------------------------------------------------------------------
# 1. Package identity.
# ---------------------------------------------------------------------------
$fromFile = @{}
if (Test-Path $IdentityFile) {
    $json = Get-Content -Raw $IdentityFile | ConvertFrom-Json
    foreach ($key in "identityName", "publisher", "publisherDisplayName", "displayName") {
        $value = $json.$key
        if (-not [string]::IsNullOrWhiteSpace($value)) { $fromFile[$key] = $value.Trim() }
    }
}
function Select-Value([string]$param, [string]$key) {
    if (-not [string]::IsNullOrWhiteSpace($param)) { return $param.Trim() }
    return $fromFile[$key]
}
$Identity = [ordered]@{
    IdentityName         = Select-Value $IdentityName "identityName"
    Publisher            = Select-Value $Publisher "publisher"
    PublisherDisplayName = Select-Value $PublisherDisplayName "publisherDisplayName"
    DisplayName          = Select-Value $DisplayName "displayName"
}
$Missing = @($Identity.Keys | Where-Object { [string]::IsNullOrWhiteSpace($Identity[$_]) })
$IsTestIdentity = $false
if ($Missing.Count -gt 0) {
    if (-not $TestIdentity) {
        Write-Error @"
The package identity is not set: $($Missing -join ', ').
Copy the values from Partner Center (Product management > Product identity) into
eng/msix/identity.json or pass them as parameters (docs/microsoft-store.md).
To check the packaging before registration, run with -TestIdentity.
"@
    }
    # Plainly a local test identity: no real publisher is named.
    $Identity = [ordered]@{
        IdentityName         = "PdfViewer.LocalTest"
        Publisher            = "CN=PdfViewer Local Test"
        PublisherDisplayName = "Local test build"
        DisplayName          = "PDF Viewer (local test)"
    }
    $IsTestIdentity = $true
    Write-Warning "Building with the local test identity. This package cannot be submitted to the Store."
}
if ($Identity.IdentityName -notmatch '^[A-Za-z0-9.\-]{3,50}$') {
    Write-Error "Identity name '$($Identity.IdentityName)' is not valid: 3 to 50 letters, digits, dots or hyphens."
}
if ($Identity.Publisher -notmatch '^CN=') {
    Write-Error "Publisher '$($Identity.Publisher)' is not valid: it is a distinguished name such as 'CN=...', copied from Partner Center."
}

# ---------------------------------------------------------------------------
# 2. Version.
# ---------------------------------------------------------------------------
if ([string]::IsNullOrEmpty($Version)) {
    $tag = "v0.0.0"
    $described = (git describe --tags --abbrev=0 --match "v[0-9]*" 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrEmpty($described)) { $tag = $described.Trim() }
    $Version = $tag.TrimStart("v", "V")
}
$parts = @($Version.Split('.'))
while ($parts.Count -lt 4) { $parts += "0" }
if ($parts.Count -ne 4 -or ($parts | Where-Object { $_ -notmatch '^\d+$' -or [int64]$_ -gt 65535 })) {
    Write-Error "Version '$Version' is not valid: up to four numbers from 0 to 65535."
}
if ($parts[3] -ne "0") {
    Write-Error "Version '$Version' is not valid for the Store: the fourth number (revision) must be 0."
}
$PackageVersion = $parts -join '.'
$AppVersion = ($parts[0..2]) -join '.'
Write-Host ">> Package $($Identity.IdentityName) $PackageVersion" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 3. Windows SDK tools.
# ---------------------------------------------------------------------------
if ([string]::IsNullOrEmpty($SdkBinDir)) {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $SdkBinDir = Get-ChildItem -Path $kits -Directory -Filter "10.*" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64" } |
        Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } |
        Select-Object -First 1
}
$MakeAppx = Join-Path "$SdkBinDir" "makeappx.exe"
$MakePri = Join-Path "$SdkBinDir" "makepri.exe"
foreach ($tool in $MakeAppx, $MakePri) {
    if (-not (Test-Path $tool)) { Write-Error "Windows SDK tool not found: $tool. Install the Windows 10/11 SDK or pass -SdkBinDir." }
}
Write-Host ">> Windows SDK: $SdkBinDir" -ForegroundColor Gray

# ---------------------------------------------------------------------------
# 4. Publish the application into the package layout.
# ---------------------------------------------------------------------------
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Path $Layout -Force | Out-Null

if ([string]::IsNullOrEmpty($AppDir)) {
    Write-Host "`n[1/5] Publishing PdfViewer v$AppVersion (self-contained, unbundled, no PDFium)..." -ForegroundColor Yellow
    dotnet publish "$RootDir\src\PdfViewer\PdfViewer.csproj" `
        -c Release `
        -p:PdfiumRenderer=false `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version="$AppVersion" `
        -p:AssemblyVersion="$AppVersion.0" `
        -p:FileVersion="$AppVersion.0" `
        -o "$Layout"
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to publish PdfViewer." }
} else {
    Write-Host "`n[1/5] Copying the application from $AppDir..." -ForegroundColor Yellow
    Copy-Item -Path (Join-Path $AppDir "*") -Destination $Layout -Recurse -Force
}

# Debug symbols do not ship in the package.
Get-ChildItem -Path $Layout -Recurse -Filter *.pdb | Remove-Item -Force

# Read Aloud is in the package. The application accepts the file only when it matches the hash
# pinned in src/PdfViewer.Core/Components/OptionalComponents.cs, so check it here.
$SpeechPin = "0E3A87AEE550BE22AC42F3BCCAEBAA914A190CD7F8AA5CE39DF2CE35B04F9D4A"
$Speech = Join-Path $Layout "System.Speech.dll"
if (-not (Test-Path $Speech)) { Write-Error "System.Speech.dll is missing from the publish: Read Aloud would not work in the package." }
$SpeechHash = (Get-FileHash $Speech -Algorithm SHA256).Hash
if ($SpeechHash -ne $SpeechPin) {
    Write-Error "System.Speech.dll ($SpeechHash) does not match the pin in OptionalComponents.cs ($SpeechPin): Read Aloud would not load."
}
Write-Host "   - Read Aloud component in the package (hash verified)" -ForegroundColor Gray

foreach ($unwanted in "pdfium.dll", "PdfEngine.Pdfium.dll") {
    if (Test-Path (Join-Path $Layout $unwanted)) { Write-Error "$unwanted is in the package layout: the shipped application has no PDFium." }
}

if (-not $NoGate) {
    Write-Host "`n>> PDFium-free gate..." -ForegroundColor Yellow
    dotnet run --project "$RootDir\eng\releasegate\ReleaseGate.Tool\ReleaseGate.Tool.csproj" -c Release -- `
        pdfium-free --app "$Layout" --out "$OutputDir\pdfium-free.json"
    if ($LASTEXITCODE -ne 0) { Write-Error "The PDFium-free gate failed (see above)." }
}

# ---------------------------------------------------------------------------
# 5. Logos, generated from assets/app_icon.png and assets/pdf_file.png, and the target sizes
#    taken from assets/app_icon.ico and assets/pdf_file.ico (all made by scripts/convert_icons.ps1
#    from the SVG masters in assets/).
# ---------------------------------------------------------------------------
Write-Host "`n[2/5] Generating the package logos..." -ForegroundColor Yellow
Add-Type -AssemblyName System.Drawing

$AssetsDir = Join-Path $Layout "Assets"
New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null

# Draws the source image centred on a transparent canvas of $width x $height. The image side is
# $fraction of the shorter canvas side, so a tile keeps a margin round its icon.
function New-Logo([System.Drawing.Image]$source, [string]$name, [int]$width, [int]$height, [double]$fraction) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bitmap.SetResolution(96, 96)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $side = [int][Math]::Round([Math]::Min($width, $height) * $fraction)
            $x = [int][Math]::Floor(($width - $side) / 2)
            $y = [int][Math]::Floor(($height - $side) / 2)
            $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # no grey edge
            $g.DrawImage($source, [System.Drawing.Rectangle]::new($x, $y, $side, $side), 0, 0, $source.Width, $source.Height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $attributes.Dispose()
        } finally { $g.Dispose() }
        $bitmap.Save((Join-Path $AssetsDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

$AppIcon = [System.Drawing.Image]::FromFile((Join-Path $RootDir "assets\app_icon.png"))
$FileIcon = [System.Drawing.Image]::FromFile((Join-Path $RootDir "assets\pdf_file.png"))
try {
    # Name, width, height, icon fraction. scale-100 and scale-200 of each logo, plus the target
    # sizes Windows uses for the taskbar, Start list and File Explorer (plated and unplated).
    $Logos = @(
        @("Square44x44Logo.scale-100.png", 44, 44, 1.0),
        @("Square44x44Logo.scale-200.png", 88, 88, 1.0),
        @("Square44x44Logo.scale-400.png", 176, 176, 1.0),
        @("Square150x150Logo.scale-100.png", 150, 150, 0.66),
        @("Square150x150Logo.scale-200.png", 300, 300, 0.66),
        @("Square150x150Logo.scale-400.png", 600, 600, 0.66),
        @("Wide310x150Logo.scale-100.png", 310, 150, 0.66),
        @("Wide310x150Logo.scale-200.png", 620, 300, 0.66),
        @("Wide310x150Logo.scale-400.png", 1240, 600, 0.66),
        @("StoreLogo.scale-100.png", 50, 50, 1.0),
        @("StoreLogo.scale-200.png", 100, 100, 1.0),
        @("StoreLogo.scale-400.png", 200, 200, 1.0)
    )
    foreach ($size in 16, 24, 32, 48, 256) {
        $Logos += , @("Square44x44Logo.targetsize-$size.png", $size, $size, 1.0)
        $Logos += , @("Square44x44Logo.targetsize-${size}_altform-unplated.png", $size, $size, 1.0)
    }
    foreach ($logo in $Logos) { New-Logo $AppIcon $logo[0] $logo[1] $logo[2] $logo[3] }

    # The .pdf file type logo (uap:Logo of the file type association).
    $FileLogos = @(@("PdfFile.scale-100.png", 44), @("PdfFile.scale-200.png", 88), @("PdfFile.scale-400.png", 176))
    foreach ($size in 16, 24, 32, 48, 256) { $FileLogos += , @("PdfFile.targetsize-$size.png", $size) }
    foreach ($logo in $FileLogos) { New-Logo $FileIcon $logo[0] $logo[1] $logo[1] 1.0 }
} finally {
    $AppIcon.Dispose()
    $FileIcon.Dispose()
}

# The target sizes are the .ico frames as they are: scripts/convert_icons.ps1 draws each one from
# its SVG at that size, and the 16 and 24 px frames from the small variants (no pen, no letters),
# which a scaled-down 512 px image would not give.
function Save-IcoFrame([string]$ico, [int]$size, [string]$name) {
    $bytes = [System.IO.File]::ReadAllBytes($ico)
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($bytes, 4); $i++) {
        $entry = 6 + 16 * $i
        $side = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        if ($side -ne $size) { continue }
        $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
        [System.IO.File]::WriteAllBytes((Join-Path $AssetsDir $name), $bytes[$offset..($offset + $length - 1)])
        return
    }
    Write-Error "$ico has no $size px frame (run scripts/convert_icons.ps1)."
}
foreach ($size in 16, 24, 32, 48, 256) {
    Save-IcoFrame (Join-Path $RootDir "assets\app_icon.ico") $size "Square44x44Logo.targetsize-$size.png"
    Save-IcoFrame (Join-Path $RootDir "assets\app_icon.ico") $size "Square44x44Logo.targetsize-${size}_altform-unplated.png"
    Save-IcoFrame (Join-Path $RootDir "assets\pdf_file.ico") $size "PdfFile.targetsize-$size.png"
}
Write-Host "   - $((Get-ChildItem $AssetsDir).Count) logo files" -ForegroundColor Gray

# ---------------------------------------------------------------------------
# 6. Manifest.
# ---------------------------------------------------------------------------
Write-Host "`n[3/5] Writing AppxManifest.xml..." -ForegroundColor Yellow
$Manifest = Get-Content -Raw (Join-Path $RootDir "eng\msix\AppxManifest.xml")
$Tokens = [ordered]@{
    "{IdentityName}"         = $Identity.IdentityName
    "{Publisher}"            = $Identity.Publisher
    "{PublisherDisplayName}" = $Identity.PublisherDisplayName
    "{DisplayName}"          = $Identity.DisplayName
    "{Version}"              = $PackageVersion
}
foreach ($token in $Tokens.Keys) {
    $Manifest = $Manifest.Replace($token, [System.Security.SecurityElement]::Escape($Tokens[$token]))
}
if ($Manifest -match '\{(IdentityName|Publisher|PublisherDisplayName|DisplayName|Version)\}') {
    Write-Error "The manifest still holds the token $($Matches[0])."
}
$null = [xml]$Manifest # fails here on malformed XML
$ManifestPath = Join-Path $Layout "AppxManifest.xml"
Set-Content -Path $ManifestPath -Value $Manifest -Encoding utf8NoBOM

# ---------------------------------------------------------------------------
# 7. resources.pri: maps "Assets\Square44x44Logo.png" in the manifest to the scale and target
#    size variants. Only the manifest and the logos are indexed, not the application files.
# ---------------------------------------------------------------------------
Write-Host "`n[4/5] Indexing the logos (resources.pri)..." -ForegroundColor Yellow
$PriWork = Join-Path $OutputDir "pri"
New-Item -ItemType Directory -Path $PriWork -Force | Out-Null
$PriRoot = Join-Path $PriWork "root"
New-Item -ItemType Directory -Path $PriRoot -Force | Out-Null
Copy-Item $AssetsDir -Destination $PriRoot -Recurse
Copy-Item $ManifestPath -Destination $PriRoot
$PriConfig = Join-Path $PriWork "priconfig.xml"
& $MakePri createconfig /cf "$PriConfig" /dq en-US /pv 10.0.0 /o | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "makepri createconfig failed." }
# One resources.pri. The default config splits the scale variants into resources.scale-*.pri for
# resource packages, which only a bundle uses; a single .msix would lose them.
[xml]$PriXml = Get-Content -Raw $PriConfig
foreach ($packaging in @($PriXml.SelectNodes("//packaging"))) { $null = $packaging.ParentNode.RemoveChild($packaging) }
$PriXml.Save($PriConfig)
& $MakePri new /pr "$PriRoot" /cf "$PriConfig" /mn "$PriRoot\AppxManifest.xml" /of "$Layout\resources.pri" /o | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "makepri new failed." }
Remove-Item -Recurse -Force $PriWork

# ---------------------------------------------------------------------------
# 8. Pack. makeappx checks the manifest against the schema and every file it names.
# ---------------------------------------------------------------------------
Write-Host "`n[5/5] Packing..." -ForegroundColor Yellow
$Suffix = if ($IsTestIdentity) { "-localtest" } else { "" }
$Msix = Join-Path $OutputDir "PdfViewer-$AppVersion-x64$Suffix.msix"
# The per-file "Processing ..." lines are left out; errors and the summary are shown.
& $MakeAppx pack /d "$Layout" /p "$Msix" /o | Where-Object { $_ -and $_ -notmatch '^Processing ' } | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "makeappx pack failed (see above)." }

if (-not [string]::IsNullOrEmpty($CertificatePfx)) {
    # Local tests only. The certificate subject must equal the manifest Publisher.
    $SignTool = Join-Path "$SdkBinDir" "signtool.exe"
    $signArgs = @("sign", "/fd", "SHA256", "/f", $CertificatePfx)
    if (-not [string]::IsNullOrEmpty($CertificatePassword)) { $signArgs += @("/p", $CertificatePassword) }
    & $SignTool @signArgs $Msix | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed: the certificate subject must equal the Publisher '$($Identity.Publisher)'." }
}

$size = [Math]::Round((Get-Item $Msix).Length / 1MB, 1)
Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " MSIX BUILT: $Msix ($size MB)" -ForegroundColor Green
Write-Host "   Identity:  $($Identity.IdentityName) / $($Identity.Publisher)" -ForegroundColor White
Write-Host "   Version:   $PackageVersion" -ForegroundColor White
Write-Host "   Signed:    $(if ($CertificatePfx) { 'test certificate' } else { 'no (the Store signs it)' })" -ForegroundColor White
Write-Host "   Layout:    $Layout" -ForegroundColor White
Write-Host "==================================================" -ForegroundColor Cyan
