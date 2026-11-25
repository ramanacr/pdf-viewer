# =====================================================================
# Signs executables with Authenticode (SHA-256) and verifies each signature.
#
# Two kinds of certificate:
# - A certificate in the Windows certificate store, chosen by its SHA-1 thumbprint: -Thumbprint,
#   or PDFVIEWER_SIGN_THUMBPRINT. This is the project's Certum certificate, whose key stays in
#   Certum's cloud: SimplySign Desktop must be connected (it shows the key as a smart card), and
#   SimplySign may ask for a confirmation. No password is handled here. A trusted certificate,
#   so the signature must verify as Valid.
# - A PFX file: -PfxPath, or PDFVIEWER_SIGN_PFX (self-signed certificates). Its password comes from
# PDFVIEWER_SIGN_PASSWORD, or from the file next to the PFX that new_signing_certificate.ps1
# writes (DPAPI-encrypted for this user, or plain for throwaway CI certificates).
# A timestamp keeps signatures valid after the certificate expires. With a store certificate
# (-Thumbprint) it is always added and checked: -TimestampUrl or PDFVIEWER_SIGN_TIMESTAMP_URL, else
# Certum's http://time.certum.pl. With a PFX it is added only when -TimestampUrl is given.
#
# Verification checks that each file's signature is intact and was made by this certificate.
# A self-signed certificate is not trusted by Windows, so the chain is reported, not required.
# =====================================================================

param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$Thumbprint = $env:PDFVIEWER_SIGN_THUMBPRINT,
    [string]$PfxPath = $env:PDFVIEWER_SIGN_PFX,
    [string]$TimestampUrl = $env:PDFVIEWER_SIGN_TIMESTAMP_URL,
    [string]$Description = "PDF Viewer",
    [string]$DescriptionUrl = "https://github.com/ramanacr/pdf-viewer"
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrEmpty($Thumbprint)) {
    $Thumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $certificate) { throw "No certificate with thumbprint $Thumbprint in CurrentUser\My. Connect SimplySign Desktop (tray icon > Connect) and try again." }
    if (-not $certificate.HasPrivateKey) { throw "The certificate $Thumbprint has no private key here. Connect SimplySign Desktop and try again." }
    if ($certificate.NotAfter -lt (Get-Date)) { throw "The certificate $Thumbprint expired on $($certificate.NotAfter)." }
    $keyArguments = @("/sha1", $Thumbprint, "/s", "My")
    $requireTrusted = $true
    # A release signature must outlive the certificate (one year), so it is always timestamped:
    # Certum's RFC 3161 service unless another one is given.
    if ([string]::IsNullOrEmpty($TimestampUrl)) { $TimestampUrl = "http://time.certum.pl" }
} else {
    if ([string]::IsNullOrEmpty($PfxPath) -or -not (Test-Path $PfxPath)) { throw "No signing certificate: set PDFVIEWER_SIGN_THUMBPRINT or PDFVIEWER_SIGN_PFX, or pass -Thumbprint or -PfxPath." }

    # The password: environment first, then the file beside the PFX.
    $password = $env:PDFVIEWER_SIGN_PASSWORD
    if ([string]::IsNullOrEmpty($password)) {
        $passwordFile = [IO.Path]::ChangeExtension($PfxPath, ".password")
        if (-not (Test-Path $passwordFile)) { throw "No password for $PfxPath (set PDFVIEWER_SIGN_PASSWORD, or keep $passwordFile beside it)." }
        $stored = (Get-Content $passwordFile -Raw).Trim()
        try {
            $secure = ConvertTo-SecureString $stored
            $password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
        } catch {
            $password = $stored # a plain password file (CI)
        }
    }
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $PfxPath, $password, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $keyArguments = @("/f", $PfxPath, "/p", $password)
    $requireTrusted = $false
}

# signtool from the newest Windows SDK.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe was not found (install the Windows 10/11 SDK signing tools)." }

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "Cannot sign ${file}: it does not exist." }
    $arguments = @("sign", "/fd", "SHA256") + $keyArguments + @("/d", $Description, "/du", $DescriptionUrl)
    if (-not [string]::IsNullOrEmpty($TimestampUrl)) { $arguments += @("/tr", $TimestampUrl, "/td", "SHA256") }
    $arguments += $file
    & $signtool.FullName @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool could not sign $file (exit code $LASTEXITCODE)." }

    # The signature must be intact and made by this certificate.
    $signature = Get-AuthenticodeSignature -FilePath $file
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "$file is not signed by $($certificate.Subject) after signing."
    }
    if ($signature.Status -in @("HashMismatch", "NotSigned", "Incompatible", "NotSupportedFileFormat")) {
        throw "$file has a broken signature: $($signature.Status) $($signature.StatusMessage)"
    }
    if ($requireTrusted -and $null -eq $signature.TimeStamperCertificate) {
        throw "$file is signed, but has no timestamp (timestamp server: $TimestampUrl)."
    }
    if ($requireTrusted -and $signature.Status -ne "Valid") {
        throw "$file is signed, but Windows does not trust the signature: $($signature.Status) $($signature.StatusMessage)"
    }
    $trust = if ($signature.Status -eq "Valid") { "trusted" } else { "not trusted by Windows (self-signed)" }
    Write-Host "  signed $([IO.Path]::GetFileName($file)): $($certificate.Thumbprint), $trust" -ForegroundColor Gray
}
