# =====================================================================
# Sets up the certificate GitHub releases are signed with (.github/workflows/release.yml).
#
# Creates a self-signed code-signing certificate for releases (or reuses the one made before),
# keeps it in %LOCALAPPDATA%\PdfViewer\release-signing with its password encrypted for this
# Windows user (DPAPI), and stores the PFX and its password as the repository secrets
# PDFVIEWER_RELEASE_PFX and PDFVIEWER_RELEASE_PFX_PASSWORD with the GitHub CLI. Nothing goes into
# the repository. Run it once; run it again with -Force to replace the certificate.
# =====================================================================

param(
    [string]$Repository = "ramanacr/pdf-viewer",
    [int]$ValidYears = 3,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$keep = Join-Path $env:LOCALAPPDATA "PdfViewer\release-signing"
$pfx = Join-Path $keep "codesign.pfx"

if ($Force -or -not (Test-Path $pfx)) {
    & "$PSScriptRoot\new_signing_certificate.ps1" -OutputDir $keep -Subject "CN=PDF Viewer Release (self-signed), O=PDF Viewer Project" -ValidYears $ValidYears -Force
}

# The password is kept DPAPI-encrypted; decrypt it only to hand it to GitHub.
$secure = ConvertTo-SecureString ((Get-Content (Join-Path $keep "codesign.password") -Raw).Trim())
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    # --body, not a pipe: PowerShell ends piped text with a newline, which would become part of the secret.
    gh secret set PDFVIEWER_RELEASE_PFX --repo $Repository --body ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
    if ($LASTEXITCODE -ne 0) { throw "gh secret set PDFVIEWER_RELEASE_PFX failed." }
    gh secret set PDFVIEWER_RELEASE_PFX_PASSWORD --repo $Repository --body $password
    if ($LASTEXITCODE -ne 0) { throw "gh secret set PDFVIEWER_RELEASE_PFX_PASSWORD failed." }
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    $password = $null
}

$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $keep "codesign.cer"))
Write-Host "Release signing is set up for $Repository." -ForegroundColor Green
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Valid to:   $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Push a tag (git tag v3.5.0; git push origin v3.5.0) to build, sign and publish a release."
