<#
.SYNOPSIS
    Fetches the rights-cleared public PDF suites into eng/vectorpdf/corpus/cache/ (git-ignored)
    at pinned commits and regenerates manifest.json (SHA-256, source, licence per file).

.DESCRIPTION
    PDFs are never committed (see README.md). Re-running with the same pins reproduces the same
    hashes; -Verify fails when a cached file no longer matches the committed manifest.

.EXAMPLE
    pwsh eng/vectorpdf/corpus/fetch-corpus.ps1
    pwsh eng/vectorpdf/corpus/fetch-corpus.ps1 -Verify
#>
param(
    [switch]$Verify,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cache = Join-Path $root 'cache'
$manifestPath = Join-Path $root 'manifest.json'

$suites = @(
    @{
        Id = 'verapdf'
        Repo = 'veraPDF/veraPDF-corpus'
        Commit = 'bb75f4f0073d9350dfd058c0162a367e6fadf25e'
        License = 'CC-BY-4.0'
        Tags = @('born-digital')
    },
    @{
        Id = 'pdf20examples'
        Repo = 'pdf-association/pdf20examples'
        Commit = 'c20f2c17bfcc4baab7cfe62e70fae64caf14d5fa'
        License = 'CC-BY-SA-4.0'
        Tags = @('born-digital')
    }
)

New-Item -ItemType Directory -Force -Path $cache | Out-Null
$entries = [System.Collections.Generic.List[object]]::new()

foreach ($suite in $suites) {
    $dir = Join-Path $cache $suite.Id
    if ($Force -and (Test-Path $dir)) { Remove-Item -Recurse -Force $dir }

    if (-not (Test-Path $dir)) {
        $zip = Join-Path $cache "$($suite.Id).zip"
        $url = "https://codeload.github.com/$($suite.Repo)/zip/$($suite.Commit)"
        Write-Host "Downloading $($suite.Repo)@$($suite.Commit.Substring(0,7))..."
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        $staging = Join-Path $cache "$($suite.Id).staging"
        if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
        Expand-Archive -Path $zip -DestinationPath $staging
        # GitHub archives contain one top-level folder named <repo>-<commit>.
        $top = Get-ChildItem $staging -Directory | Select-Object -First 1
        Move-Item $top.FullName $dir
        Remove-Item -Recurse -Force $staging
        Remove-Item -Force $zip
    }

    Get-ChildItem $dir -Recurse -File -Filter *.pdf | Sort-Object FullName | ForEach-Object {
        $rel = [System.IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\', '/')
        $entries.Add([ordered]@{
            id = "$($suite.Id)/$rel"
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            source = "https://github.com/$($suite.Repo)/blob/$($suite.Commit)/$([uri]::EscapeUriString($rel))"
            license = $suite.License
            redistributable = $false   # policy: never committed here, even when the licence would allow it
            tags = $suite.Tags
        })
    }
}

if ($Verify) {
    $committed = (Get-Content $manifestPath -Raw | ConvertFrom-Json).entries
    $byId = @{}
    foreach ($e in $entries) { $byId[$e.id] = $e.sha256 }
    $bad = @($committed | Where-Object { $byId[$_.id] -ne $_.sha256 })
    if ($bad.Count -gt 0) {
        $bad | Select-Object -First 10 | ForEach-Object { Write-Error "hash mismatch or missing: $($_.id)" -ErrorAction Continue }
        exit 1
    }
    Write-Host "verified $($committed.Count) corpus files"
    exit 0
}

[ordered]@{ entries = $entries } | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "manifest: $($entries.Count) files -> $manifestPath"
