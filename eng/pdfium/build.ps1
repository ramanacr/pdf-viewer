# =====================================================================
# PDFium Setup & Verification Script
# Downloads and stages pinned PDFium native binaries for Windows x64.
# =====================================================================

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$VersionJsonPath = Join-Path $ScriptDir "version.json"

if (!(Test-Path $VersionJsonPath)) {
    Write-Error "version.json not found at $VersionJsonPath"
}

$versionInfo = Get-Content $VersionJsonPath -Raw | ConvertFrom-Json

$tag = $versionInfo.tag
$assetUrl = $versionInfo.asset_url
$expectedHash = $versionInfo.sha256
$tarPath = Join-Path $ScriptDir "pdfium-win-x64.tgz"

Write-Host "PDFium Version: $($versionInfo.version) ($tag)" -ForegroundColor Cyan
Write-Host "Target Architecture: $($versionInfo.target_cpu)" -ForegroundColor Cyan

# Download if not already present or hash mismatch
$needDownload = $true
if (Test-Path $tarPath) {
    $actualHash = (Get-FileHash -Path $tarPath -Algorithm SHA256).Hash
    if ($actualHash -eq $expectedHash) {
        Write-Host "Cached archive valid (SHA256 verified): $actualHash" -ForegroundColor Green
        $needDownload = $false
    }
}

if ($needDownload) {
    Write-Host "Downloading PDFium from $assetUrl..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $assetUrl -OutFile $tarPath
    $actualHash = (Get-FileHash -Path $tarPath -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        Remove-Item $tarPath -Force -ErrorAction SilentlyContinue
        Write-Error "SHA256 mismatch! Expected: $expectedHash, Actual: $actualHash"
    }
    Write-Host "Download verified successfully (SHA256: $actualHash)" -ForegroundColor Green
}

# Extract archive
Write-Host "Extracting PDFium archive..." -ForegroundColor Gray
tar -xzf $tarPath -C $ScriptDir

# Copy native library to runtime directory
$runtimeNativeDir = Join-Path $RootDir "src\PdfViewer\runtimes\win-x64\native"
New-Item -ItemType Directory -Path $runtimeNativeDir -Force | Out-Null

$srcDll = Join-Path $ScriptDir "bin\pdfium.dll"
$destDll = Join-Path $runtimeNativeDir "pdfium.dll"

Copy-Item $srcDll $destDll -Force
Write-Host "Staged pdfium.dll to $destDll" -ForegroundColor Green

# Stage third-party notices
$licenseSrc = Join-Path $ScriptDir "LICENSE"
$noticesDest = Join-Path $RootDir "THIRD_PARTY_NOTICES.md"

if (Test-Path $licenseSrc) {
    $licenseContent = Get-Content $licenseSrc -Raw
    $notices = "# Third-Party Software Notices and Licenses`n`nThis product includes software developed by Google Inc. (Google PDFium).`n`n## Google PDFium (Chromium) License`nVersion: $($versionInfo.version) ($($versionInfo.tag))`nSource: https://pdfium.googlesource.com/pdfium/`n`n``````n" + $licenseContent + "``````n"
    Set-Content -Path $noticesDest -Value $notices -Encoding UTF8
    Write-Host "Created $noticesDest" -ForegroundColor Green
}

Write-Host "PDFium setup completed successfully." -ForegroundColor Green
