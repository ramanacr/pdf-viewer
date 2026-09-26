<#
.SYNOPSIS
    Downloads the predefined CJK CMaps of ISO 32000-2 Table 116 from Adobe's cmap-resources
    (BSD-3-Clause) at a pinned commit and packs them into the embedded resource
    src/PdfEngine.Vector/Fonts/CMaps/PredefinedCMaps.zip, plus the licence and a SHA-256 manifest.

.DESCRIPTION
    Re-running with the same pin reproduces the same files; -Verify fails when the committed zip
    no longer matches cmaps-manifest.json.

.EXAMPLE
    pwsh eng/vectorpdf/cmaps/build-cmaps.ps1
    pwsh eng/vectorpdf/cmaps/build-cmaps.ps1 -Verify
#>
param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$repo = 'adobe-type-tools/cmap-resources'
$commit = 'f5cf3bca7fdfeaceb77aa82847e974f2306c20b4'
$root = Resolve-Path (Join-Path $PSScriptRoot '../../..')
$outDir = Join-Path $root 'src/PdfEngine.Vector/Fonts/CMaps'
$zipPath = Join-Path $outDir 'PredefinedCMaps.zip'
$manifestPath = Join-Path $PSScriptRoot 'cmaps-manifest.json'

$collections = [ordered]@{
    'Adobe-GB1-6'   = 'GB-EUC-H GB-EUC-V GBpc-EUC-H GBpc-EUC-V GBK-EUC-H GBK-EUC-V GBKp-EUC-H GBKp-EUC-V GBK2K-H GBK2K-V UniGB-UCS2-H UniGB-UCS2-V UniGB-UTF16-H UniGB-UTF16-V'
    'Adobe-CNS1-7'  = 'B5pc-H B5pc-V HKscs-B5-H HKscs-B5-V ETen-B5-H ETen-B5-V ETenms-B5-H ETenms-B5-V CNS-EUC-H CNS-EUC-V UniCNS-UCS2-H UniCNS-UCS2-V UniCNS-UTF16-H UniCNS-UTF16-V'
    'Adobe-Japan1-7' = '83pv-RKSJ-H 90ms-RKSJ-H 90ms-RKSJ-V 90msp-RKSJ-H 90msp-RKSJ-V 90pv-RKSJ-H Add-RKSJ-H Add-RKSJ-V EUC-H EUC-V Ext-RKSJ-H Ext-RKSJ-V H V UniJIS-UCS2-H UniJIS-UCS2-V UniJIS-UCS2-HW-H UniJIS-UCS2-HW-V UniJIS-UTF16-H UniJIS-UTF16-V'
    'Adobe-Korea1-2' = 'KSC-EUC-H KSC-EUC-V KSCms-UHC-H KSCms-UHC-V KSCms-UHC-HW-H KSCms-UHC-HW-V KSCpc-EUC-H UniKS-UCS2-H UniKS-UCS2-V UniKS-UTF16-H UniKS-UTF16-V'
}

function Get-Sha256([byte[]]$bytes) {
    [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

if ($Verify) {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    Add-Type -AssemblyName System.IO.Compression
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($e in $manifest.files) {
            $entry = $zip.GetEntry($e.name)
            if ($null -eq $entry) { throw "missing $($e.name)" }
            $ms = [System.IO.MemoryStream]::new()
            $s = $entry.Open(); $s.CopyTo($ms); $s.Dispose()
            if ((Get-Sha256 $ms.ToArray()) -ne $e.sha256) { throw "hash mismatch $($e.name)" }
        }
    } finally { $zip.Dispose() }
    Write-Host "PredefinedCMaps.zip matches the manifest ($($manifest.files.Count) CMaps)."
    return
}

New-Item -ItemType Directory -Force $outDir | Out-Null
$entries = @()
$files = [ordered]@{}
foreach ($collection in $collections.Keys) {
    foreach ($name in $collections[$collection].Split(' ')) {
        $url = "https://raw.githubusercontent.com/$repo/$commit/$collection/CMap/$name"
        $bytes = (Invoke-WebRequest -Uri $url -UseBasicParsing).Content
        if ($bytes -is [string]) { $bytes = [System.Text.Encoding]::Latin1.GetBytes($bytes) }
        $files[$name] = $bytes
        $entries += [ordered]@{ name = $name; collection = $collection; bytes = $bytes.Length; sha256 = Get-Sha256 $bytes }
    }
}

$license = (Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$repo/$commit/LICENSE.md" -UseBasicParsing).Content
Set-Content -Path (Join-Path $outDir 'LICENSE-cmap-resources.md') -Value $license -NoNewline

Add-Type -AssemblyName System.IO.Compression
if (Test-Path $zipPath) { Remove-Item $zipPath }
$fs = [System.IO.File]::Create($zipPath)
$zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $files.Keys) {
        $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::SmallestSize)
        $entry.LastWriteTime = [DateTimeOffset]::new(2023, 11, 15, 0, 0, 0, [TimeSpan]::Zero)
        $s = $entry.Open(); $s.Write($files[$name], 0, $files[$name].Length); $s.Dispose()
    }
} finally { $zip.Dispose(); $fs.Dispose() }

[ordered]@{
    source = "https://github.com/$repo"
    commit = $commit
    license = 'BSD-3-Clause'
    files = $entries
} | ConvertTo-Json -Depth 4 | Set-Content $manifestPath
Write-Host "Packed $($entries.Count) CMaps into $zipPath ($((Get-Item $zipPath).Length) bytes)."
