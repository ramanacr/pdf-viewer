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

# 1. Clean previous publish folder
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

# 2. Publish the main WPF application
Write-Host "`n[1/4] Publishing main PDF Viewer application (including inbuilt Aspose license)..." -ForegroundColor Yellow
dotnet publish "$RootDir\src\PdfViewer\PdfViewer.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$AppStagingDir"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish PdfViewer application."
}

# 3. Create Payload.zip for the installer
Write-Host "`n[2/4] Creating installer payload archive..." -ForegroundColor Yellow
Compress-Archive -Path "$AppStagingDir\*" -DestinationPath "$PayloadZip" -Force

# 4. Build and publish the Windows Setup Installer
Write-Host "`n[3/4] Building Windows Setup Installer (PdfViewerSetup.exe)..." -ForegroundColor Yellow
$InstallerStaging = Join-Path $PublishDir "installer_staging"
New-Item -ItemType Directory -Path $InstallerStaging -Force | Out-Null

dotnet publish "$RootDir\src\Installer\PdfViewerInstaller.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
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

# Clean up staging folders & temporary zip
Remove-Item -Path $AppStagingDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $InstallerStaging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $PayloadZip -Force -ErrorAction SilentlyContinue

Write-Host "`n[4/4] Package Verification:" -ForegroundColor Green
Get-ChildItem -Path $PublishDir | Format-Table Name, Length, LastWriteTime

Write-Host "`n==================================================" -ForegroundColor Cyan
Write-Host " PUBLISH COMPLETED SUCCESSFULLY!                 " -ForegroundColor Green
Write-Host " Output folder: $PublishDir" -ForegroundColor White
Write-Host "   • Setup Installer:  $PublishDir\PdfViewerSetup.exe" -ForegroundColor White
Write-Host "   • Standalone App:   $PublishDir\PdfViewer.exe" -ForegroundColor White
Write-Host "==================================================" -ForegroundColor Cyan
