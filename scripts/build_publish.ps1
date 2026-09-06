# =====================================================================
# Build and Package Script for PDF Viewer Native
# Produces:
#   - publish/PdfViewerSetup.exe  (Windows Installable Setup Executable)
#   - publish/PdfViewer.exe       (Standalone Portable Single-File Executable)
#   - publish/SampleDocument.pdf  (Demo Test Document)
# =====================================================================

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
Set-Location $RootDir

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Building & Packaging PDF Viewer Native (.NET 9)  " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Determine the app version from the nearest git release tag (e.g. "v1.2.2" -> 1.2.2).
#    This MUST match Directory.Build.props' AutoSetGitVersion target and MUST match the
#    tag this build is published under: UpdateService.CompareVersions compares the
#    installed assembly version directly against GitHub release tag names, so a build
#    versioned from anything else (e.g. raw commit count) silently breaks self-update
#    detection once the two numbering schemes drift apart.
$LatestTag = "v0.0.0"
try {
    $tag = (git describe --tags --abbrev=0 2>$null)
    if (![string]::IsNullOrEmpty($tag)) {
        $LatestTag = $tag.Trim()
    }
} catch {
    $LatestTag = "v0.0.0"
}

$AppVersion = $LatestTag.TrimStart("v", "V")
$AssemblyVersion = "$AppVersion.0"

Write-Host "`n>> Product Version: $AppVersion (from git tag $LatestTag)" -ForegroundColor Green

# 2. Clean previous publish folder
$PublishDir = Join-Path $RootDir "publish"
$AppStagingDir = Join-Path $PublishDir "app"
$InstallerDir = Join-Path $RootDir "src\Installer"
$PayloadZip = Join-Path $InstallerDir "Payload.zip"

if (Test-Path $PublishDir) {
    Write-Host "Cleaning publish directory..." -ForegroundColor Gray
    Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path $PayloadZip) {
    Remove-Item -Path $PayloadZip -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $AppStagingDir -Force | Out-Null

# Ensure asset directories have all the latest icons
if (Test-Path "$RootDir\assets") {
    Copy-Item -Path "$RootDir\assets\*" -Destination "$RootDir\src\PdfViewer\assets" -Force -ErrorAction SilentlyContinue
    Copy-Item -Path "$RootDir\assets\*" -Destination "$RootDir\src\Installer\assets" -Force -ErrorAction SilentlyContinue
}

# 3. Publish the main WPF application
Write-Host "`n[1/4] Publishing main PDF Viewer application v$AppVersion (with Google PDFium native engine and icons)..." -ForegroundColor Yellow
dotnet publish "$RootDir\src\PdfViewer\PdfViewer.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version="$AppVersion" `
    -p:AssemblyVersion="$AssemblyVersion" `
    -p:FileVersion="$AssemblyVersion" `
    -o "$AppStagingDir"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish PdfViewer application."
}

# Copy assets folder into AppStagingDir so the installer delivers them to the user directory
if (Test-Path "$RootDir\assets") {
    Copy-Item -Path "$RootDir\assets" -Destination "$AppStagingDir\assets" -Recurse -Force
}

# ---------------------------------------------------------------------------
# Optional components.
#
# These are deliberately NOT put in the installer payload: a user who does not
# want Read Aloud should not carry it. They ship as release assets instead, and
# are downloaded on request by the installer or the application.
#
# The hash pinned in OptionalComponents.cs is what both of those check the
# download against, so a mismatch here means every download would be rejected
# in the field. Fail the build now rather than ship that.
# ---------------------------------------------------------------------------
$OptionalComponents = @(
    @{ File = "System.Speech.dll"; Sha256 = "0E3A87AEE550BE22AC42F3BCCAEBAA914A190CD7F8AA5CE39DF2CE35B04F9D4A"; Name = "Read Aloud" }
)

Write-Host "`n>> Separating optional components from the installer payload..." -ForegroundColor Yellow
foreach ($component in $OptionalComponents) {
    $staged = Join-Path $AppStagingDir $component.File

    if (-not (Test-Path $staged)) {
        Write-Error "Optional component '$($component.File)' was not produced by the publish. It must be excluded from the single-file bundle, not from the build."
    }

    $actual = (Get-FileHash $staged -Algorithm SHA256).Hash
    if ($actual -ne $component.Sha256) {
        Write-Error @"
Optional component '$($component.File)' does not match the hash pinned in
src/PdfViewer.Core/Components/OptionalComponents.cs.

  expected $($component.Sha256)
  actual   $actual

The application and installer both verify downloads against the pinned value, so
shipping this build would make '$($component.Name)' impossible to install. Update
the pin (and this script) to the new hash if the change is intended.
"@
    }

    # Out of the payload, into the release assets.
    Move-Item -Path $staged -Destination (Join-Path $PublishDir $component.File) -Force
    Write-Host "   - $($component.File) -> release asset ($($component.Name), verified)" -ForegroundColor Gray
}

# 4. Create Payload.zip for the installer
Write-Host "`n[2/4] Creating installer payload archive..." -ForegroundColor Yellow
Compress-Archive -Path "$AppStagingDir\*" -DestinationPath "$PayloadZip" -Force

# 5. Build and publish the Windows Setup Installer
Write-Host "`n[3/4] Building Windows Setup Installer v$AppVersion (PdfViewerSetup.exe)..." -ForegroundColor Yellow
$InstallerStaging = Join-Path $PublishDir "installer_staging"
New-Item -ItemType Directory -Path $InstallerStaging -Force | Out-Null

dotnet publish "$RootDir\src\Installer\PdfViewerInstaller.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version="$AppVersion" `
    -p:AssemblyVersion="$AssemblyVersion" `
    -p:FileVersion="$AssemblyVersion" `
    -o "$InstallerStaging"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish PdfViewerSetup installer."
}

# Move final artifacts to publish root
Move-Item -Path "$InstallerStaging\PdfViewerSetup.exe" -Destination "$PublishDir\PdfViewerSetup.exe" -Force
Copy-Item -Path "$AppStagingDir\PdfViewer.exe" -Destination "$PublishDir\PdfViewer.exe" -Force

# Copy sample test document
if (Test-Path "$RootDir\samples\SampleDocument.pdf") {
    Copy-Item -Path "$RootDir\samples\SampleDocument.pdf" -Destination "$PublishDir\SampleDocument.pdf" -Force
}

# Copy icons to publish directory
if (Test-Path "$RootDir\assets") {
    Copy-Item -Path "$RootDir\assets" -Destination "$PublishDir\assets" -Recurse -Force
}

# Copy third party notices
if (Test-Path "$RootDir\THIRD_PARTY_NOTICES.md") {
    Copy-Item -Path "$RootDir\THIRD_PARTY_NOTICES.md" -Destination "$PublishDir\THIRD_PARTY_NOTICES.md" -Force
}

# 6. Generate Software Bill of Materials (SBOM)
Write-Host "`n[4/5] Generating Software Bill of Materials (SBOM)..." -ForegroundColor Yellow
& "$ScriptDir\generate_sbom.ps1" -OutputDir "$PublishDir" -Version "$AppVersion"

# Clean up staging folders & temporary zip
Remove-Item -Path $AppStagingDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $InstallerStaging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $PayloadZip -Force -ErrorAction SilentlyContinue

Write-Host "`n[5/5] Package Verification:" -ForegroundColor Green
Get-ChildItem -Path $PublishDir | Format-Table Name, Length, LastWriteTime

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " PUBLISH COMPLETED SUCCESSFULLY!                 " -ForegroundColor Green
Write-Host " Output folder: $PublishDir" -ForegroundColor White
Write-Host "   • Setup Installer:  $PublishDir\PdfViewerSetup.exe" -ForegroundColor White
Write-Host "   • Standalone App:   $PublishDir\PdfViewer.exe" -ForegroundColor White
Write-Host "   • CycloneDX SBOM:   $PublishDir\sbom.cyclonedx.json" -ForegroundColor White
Write-Host "   • SPDX SBOM:        $PublishDir\sbom.spdx.json" -ForegroundColor White
Write-Host "==================================================" -ForegroundColor Cyan
