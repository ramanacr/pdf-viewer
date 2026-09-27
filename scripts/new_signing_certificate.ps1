# =====================================================================
# Creates a self-signed Authenticode code-signing certificate for PDF Viewer builds.
#
# The certificate is written as a password-protected PFX (and its public .cer) to a folder
# outside the repository; it is NOT installed into any Windows certificate store and nothing
# is trusted. Binaries signed with it carry a valid signature and a stable publisher identity,
# but Windows reports the publisher as unknown until a certificate from a trusted CA is used
# (eng/signing/README.md).
#
# The PFX password is kept next to it, encrypted for the current Windows user (DPAPI), so only
# this account on this machine can use the key. -PlainPasswordFile writes it in clear instead
# (for throwaway CI certificates).
# =====================================================================

param(
    [string]$OutputDir = (Join-Path $env:LOCALAPPDATA "PdfViewer\signing"),
    [string]$Subject = "CN=PDF Viewer (self-signed), O=PDF Viewer Project",
    [int]$ValidYears = 3,
    [switch]$PlainPasswordFile,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$pfxPath = Join-Path $OutputDir "codesign.pfx"
$cerPath = Join-Path $OutputDir "codesign.cer"
$passwordPath = Join-Path $OutputDir "codesign.password"

if ((Test-Path $pfxPath) -and -not $Force) {
    Write-Host "A signing certificate already exists at $pfxPath (use -Force to replace it)." -ForegroundColor Yellow
    return
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Add-Type -AssemblyName System.Security
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
$eku = [System.Security.Cryptography.OidCollection]::new()
[void]$eku.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.3", "Code Signing"))
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
$request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))

$notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
$certificate = $request.CreateSelfSigned($notBefore, $notBefore.AddYears($ValidYears))

# A random password: 32 bytes from the system RNG.
$bytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$password = [Convert]::ToBase64String($bytes)

[IO.File]::WriteAllBytes($pfxPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
[IO.File]::WriteAllBytes($cerPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
if ($PlainPasswordFile) {
    Set-Content -Path $passwordPath -Value $password -NoNewline
} else {
    ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -Path $passwordPath -NoNewline
}

Write-Host "Created a self-signed code-signing certificate:" -ForegroundColor Green
Write-Host "  Subject:    $($certificate.Subject)"
Write-Host "  Thumbprint: $($certificate.Thumbprint)"
Write-Host "  Valid:      $($certificate.NotBefore.ToString('yyyy-MM-dd')) to $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  PFX:        $pfxPath"
Write-Host "  Public:     $cerPath"
Write-Host "Builds sign with it when PDFVIEWER_SIGN_PFX points at the PFX (build_publish.ps1 uses this location by default)."
