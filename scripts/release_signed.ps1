# =====================================================================
# Builds, signs and uploads a GitHub release as a DRAFT, on this PC, with the project's Certum
# code signing certificate (eng/signing/README.md).
#
# The certificate's key stays in Certum's cloud. SimplySign Desktop must be connected (tray icon >
# Connect, with the token from the SimplySign phone app), so Windows sees the key as a smart card.
# This script never asks for or handles the SimplySign password or token; SimplySign may ask for a
# confirmation while signing.
#
#   git checkout v4.4.0          # the release tag, pushed to GitHub
#   ./scripts/release_signed.ps1 -Tag v4.4.0
#
# What it does:
#   1. Checks that the working tree is clean, HEAD is the tag, and the tag is on GitHub.
#   2. Checks the certificate (thumbprint below) is present, has its key and has not expired.
#   3. Runs scripts/build_publish.ps1, which signs PdfViewer.exe before it goes into the
#      installer, then PdfViewerSetup.exe, each with a Certum timestamp.
#   4. Checks both signatures: valid, trusted, made by this certificate, timestamped.
#   5. Creates (or updates) the DRAFT release with the files. It never touches a published release.
# Then look at the draft on GitHub and publish it there.
# =====================================================================

param(
    [Parameter(Mandatory = $true)][string]$Tag,
    # The Certum certificate "Open Source Developer Ramana Reddy Chamakura" (valid to 2027-11-25).
    # A public value: it names the certificate, it is not a secret.
    [string]$Thumbprint = "68572404AE99971D76B907D9452001DCEE7766F1",
    # Certum's RFC 3161 timestamp service: signatures stay valid after the certificate expires.
    [string]$TimestampUrl = "http://time.certum.pl",
    [string]$Repository = "ramanacr/pdf-viewer"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
Push-Location $RootDir
try {
    # 1. The source: exactly the pushed tag, with no local changes.
    if (git status --porcelain --untracked-files=no) { throw "The working tree has changes. Commit or stash them, then check out $Tag." }
    $headTags = @(git tag --points-at HEAD)
    if ($headTags -notcontains $Tag) { throw "HEAD is not the tag $Tag. Run: git checkout $Tag" }
    $remote = git ls-remote --tags origin "refs/tags/$Tag"
    if (-not $remote) { throw "The tag $Tag is not on GitHub. Push it first: git push origin $Tag" }
    $localSha = git rev-parse "$Tag^{commit}"
    $remoteSha = git ls-remote origin "refs/tags/$Tag^{}" | ForEach-Object { ($_ -split '\s+')[0] }
    if (-not $remoteSha) { $remoteSha = ($remote -split '\s+')[0] }
    if ($remoteSha -ne $localSha) { throw "The tag $Tag on GitHub points to $remoteSha, not to the local $localSha." }

    # The release, if any: only a draft may be updated.
    $existing = gh release view $Tag --repo $Repository --json isDraft 2>$null | ConvertFrom-Json
    if ($existing -and -not $existing.isDraft) { throw "The release $Tag is already published. This script only creates or updates drafts." }

    # 2. The certificate, through SimplySign.
    $Thumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $certificate -or -not $certificate.HasPrivateKey) {
        throw "The signing certificate $Thumbprint is not available. Connect SimplySign Desktop (tray icon > Connect) and try again."
    }
    if ($certificate.NotAfter -lt (Get-Date).AddDays(1)) { throw "The signing certificate expires on $($certificate.NotAfter); renew it first." }
    Write-Host ">> Signing with $($certificate.Subject) ($Thumbprint), valid to $($certificate.NotAfter.ToString('yyyy-MM-dd'))" -ForegroundColor Yellow

    # 3. Build and sign. Only this certificate: a PFX from the environment must not be used.
    $env:PDFVIEWER_SIGN_THUMBPRINT = $Thumbprint
    $env:PDFVIEWER_SIGN_TIMESTAMP_URL = $TimestampUrl
    Remove-Item Env:PDFVIEWER_SIGN_PFX, Env:PDFVIEWER_SIGN_PASSWORD -ErrorAction SilentlyContinue
    & "$PSScriptRoot\build_publish.ps1"
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "build_publish.ps1 failed (exit code $LASTEXITCODE)." }

    # 4. The signatures.
    $publish = Join-Path $RootDir "publish"
    foreach ($name in "PdfViewer.exe", "PdfViewerSetup.exe") {
        $file = Join-Path $publish $name
        $s = Get-AuthenticodeSignature -FilePath $file
        if ($null -eq $s.SignerCertificate -or $s.SignerCertificate.Thumbprint -ne $Thumbprint) { throw "$name is not signed by the release certificate." }
        if ($s.Status -ne "Valid") { throw "$name has a signature that Windows does not trust: $($s.Status) $($s.StatusMessage)" }
        if ($null -eq $s.TimeStamperCertificate) { throw "$name has no timestamp." }
        Write-Host "  $name signed by $($s.SignerCertificate.Subject), timestamped by $($s.TimeStamperCertificate.Subject)" -ForegroundColor Gray
    }
    [IO.File]::WriteAllBytes((Join-Path $publish "PdfViewer-codesign.cer"),
        $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    # 5. The draft release.
    $assets = @("PdfViewerSetup.exe", "PdfViewer.exe", "PdfViewer-codesign.cer", "SampleDocument.pdf", "System.Speech.dll",
                "THIRD_PARTY_NOTICES.md", "sbom.cyclonedx.json", "sbom.spdx.json") |
        ForEach-Object { Join-Path $publish $_ } | Where-Object { Test-Path $_ }
    $notes = @"
## Signed download

``PdfViewerSetup.exe`` and ``PdfViewer.exe`` are Authenticode-signed (SHA-256) and timestamped with a code signing certificate issued by **Certum** (Asseco Data Systems S.A.) to **$($certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false))**.

- Certificate thumbprint (SHA-1): ``$Thumbprint``
- Valid until $($certificate.NotAfter.ToString('yyyy-MM-dd')); the timestamp keeps the signatures valid after that date

To check a download, run this in PowerShell; it must say ``Valid`` and show the thumbprint above (``PdfViewer-codesign.cer`` is the certificate's public part):

``````powershell
Get-AuthenticodeSignature .\PdfViewerSetup.exe | Format-List Status, @{n='Thumbprint';e={`$_.SignerCertificate.Thumbprint}}
``````

See the [code signing policy](https://github.com/$Repository/blob/main/CODE_SIGNING_POLICY.md) and the [MIT licence](https://github.com/$Repository/blob/main/LICENSE).
"@
    $notesFile = Join-Path ([IO.Path]::GetTempPath()) "pdfviewer-release-notes-$Tag.md"
    Set-Content -Path $notesFile -Value $notes -Encoding utf8
    if ($existing) {
        gh release upload $Tag @assets --repo $Repository --clobber
        if ($LASTEXITCODE -eq 0) { gh release edit $Tag --repo $Repository --notes-file $notesFile }
    } else {
        gh release create $Tag @assets --repo $Repository --title $Tag --notes-file $notesFile --verify-tag --draft
    }
    if ($LASTEXITCODE -ne 0) { throw "Uploading the draft release failed." }
    Remove-Item $notesFile -ErrorAction SilentlyContinue

    Write-Host "`nDraft release $Tag is ready. Check it, then publish it on GitHub:" -ForegroundColor Green
    Write-Host "  https://github.com/$Repository/releases" -ForegroundColor Green
} finally {
    Pop-Location
}
