# =====================================================================
# Software Bill of Materials (SBOM) Generator for PDF Viewer
# Generates:
#   - sbom.cyclonedx.json (CycloneDX v1.6 Specification)
#   - sbom.spdx.json       (SPDX v2.3 Specification)
#
# It describes what the published app ships: the build scripts/build_publish.ps1 makes with
# -p:PdfiumRenderer=false. That build has no PDFium (no pdfium.dll, no PdfEngine.Pdfium); PDFium is
# the reference renderer of developer and test builds only (its licences are in eng/pdfium).
# =====================================================================

param(
    [string]$OutputDir = "$PSScriptRoot\..\publish",
    [string]$Version = "2.0.0"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$Timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$DocGuid = [Guid]::NewGuid().ToString()

Write-Host "Generating Software Bill of Materials (SBOM) for PDF Viewer v$Version..." -ForegroundColor Cyan

# ---------------------------------------------------------------------
# 1. First-party assemblies in the published app, and bundled data.
# ---------------------------------------------------------------------
$FirstParty = @(
    @{ Name = "PdfEngine.Abstractions"; Description = "UI-neutral domain contracts, geometry, exception hierarchy, and abstractions for PDF processing" },
    @{ Name = "PdfEngine.Vector"; Description = "The PDF engine: parsing, security, fonts, text, forms, annotations, signatures, redaction, saving and the vector display list" },
    @{ Name = "PdfEngine.Vector.Windows"; Description = "WPF rendering of the engine's display lists" },
    @{ Name = "PdfEngine.Vector.Direct2D"; Description = "Direct2D/DirectWrite rendering and printing of the engine's display lists" },
    @{ Name = "PdfViewer.Core"; Description = "DocumentSession, Priority Render Scheduler, MultiTierCache, and Command History infrastructure" }
)
$CmapCommit = "f5cf3bca7fdfeaceb77aa82847e974f2306c20b4"

# ---------------------------------------------------------------------
# 1b. Every NuGet package that ships, derived from the projects (not written by hand, so the
#     SBOM cannot fall behind a new or updated reference). The application is restored as it is
#     published (PdfiumRenderer=false), so a package only the PDFium oracle needs is not listed.
#     Licences come from each package's own .nuspec. A package whose licence cannot be read
#     fails the build.
# ---------------------------------------------------------------------
function Get-ShippedPackages {
    $projects = @("$RootDir\src\PdfViewer\PdfViewer.csproj", "$RootDir\src\Installer\PdfViewerInstaller.csproj")
    $found = [ordered]@{}
    foreach ($project in $projects) {
        # The published configuration restores into obj\nopdfium (Directory.Build.props). `dotnet
        # list package` cannot be given that property, so the restore graph is read directly.
        & dotnet restore $project -p:PdfiumRenderer=false --verbosity quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $project." }
        $assetsFile = Join-Path (Split-Path -Parent $project) "obj\nopdfium\project.assets.json"
        if (-not (Test-Path $assetsFile)) { throw "No restore graph at $assetsFile." }
        $assets = Get-Content -Raw $assetsFile | ConvertFrom-Json
        foreach ($lib in $assets.libraries.PSObject.Properties) {
            if ($lib.Value.type -ne "package") { continue }
            $id, $version = $lib.Name.Split('/')
            $key = "$id@$version"
            if (-not $found.Contains($key)) { $found[$key] = [pscustomobject]@{ Id = $id; Version = $version } }
        }
    }
    $root = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
    foreach ($pkg in $found.Values) {
        $nuspec = Join-Path $root ("{0}\{1}\{0}.nuspec" -f $pkg.Id.ToLowerInvariant(), $pkg.Version.ToLowerInvariant())
        if (-not (Test-Path $nuspec)) { throw "No .nuspec for $($pkg.Id) $($pkg.Version) at $nuspec." }
        $meta = ([xml](Get-Content -Raw $nuspec)).package.metadata
        $license = if ($meta.license -and $meta.license.type -eq "expression") { $meta.license.'#text' } elseif ($meta.license) { "LicenseRef-" + ($meta.license.'#text' -replace '[^A-Za-z0-9.-]', '-') } else { $null }
        if (-not $license -and $meta.licenseUrl) { $license = "LicenseRef-see-" + ($meta.licenseUrl -replace '[^A-Za-z0-9.-]', '-') }
        if (-not $license) { throw "The licence of $($pkg.Id) $($pkg.Version) cannot be read from its .nuspec." }
        $pkg | Add-Member License $license
        $pkg | Add-Member Project ([string]$meta.projectUrl)
        $pkg | Add-Member Copyright ([string]$meta.copyright)
        $pkg | Add-Member Authors ([string]$meta.authors)
    }
    return @($found.Values)
}

$ShippedPackages = Get-ShippedPackages
Write-Host "  -> $($ShippedPackages.Count) shipped NuGet package(s) found" -ForegroundColor Gray

# ---------------------------------------------------------------------
# 2. Generate CycloneDX v1.6 JSON
# ---------------------------------------------------------------------
$MitLicense = @([ordered]@{ license = [ordered]@{ id = "MIT"; url = "https://opensource.org/licenses/MIT" } })
$CmapRef = "pkg:github/adobe-type-tools/cmap-resources@$CmapCommit"

$CycloneDx = [ordered]@{
    bomFormat    = "CycloneDX"
    specVersion  = "1.6"
    serialNumber = "urn:uuid:$DocGuid"
    version      = 1
    metadata     = [ordered]@{
        timestamp = $Timestamp
        tools     = [ordered]@{
            components = @(
                [ordered]@{
                    type    = "application"
                    name    = "PDF Viewer SBOM Generator"
                    version = "3.0.0"
                }
            )
        }
        authors   = @(
            [ordered]@{
                name = "PDF Viewer Project"
            }
        )
        component = [ordered]@{
            type        = "application"
            name        = "PdfViewer"
            version     = $Version
            description = "Native Windows PDF Viewer with its own PDF engine (PdfEngine.Vector)"
            licenses    = $MitLicense
            purl        = "pkg:github/ramanacr/pdf-viewer@$Version"
        }
    }
    components   = @()
    dependencies = @(
        [ordered]@{
            ref       = "PdfViewer@$Version"
            dependsOn = @()
        }
    )
}

foreach ($fp in $FirstParty) {
    $purl = "pkg:github/ramanacr/pdf-viewer/$($fp.Name)@$Version"
    $CycloneDx.components = @($CycloneDx.components) + [ordered]@{
        type        = "library"
        "bom-ref"   = $purl
        name        = $fp.Name
        version     = $Version
        description = $fp.Description
        scope       = "required"
        licenses    = $MitLicense
        purl        = $purl
    }
    $CycloneDx.dependencies[0].dependsOn = @($CycloneDx.dependencies[0].dependsOn) + $purl
}

# Data: Adobe's predefined CJK CMaps, embedded in PdfEngine.Vector.
$CycloneDx.components = @($CycloneDx.components) + [ordered]@{
    type        = "data"
    "bom-ref"   = $CmapRef
    name        = "cmap-resources"
    version     = $CmapCommit
    description = "Adobe predefined CJK CMaps (PredefinedCMaps.zip, embedded in PdfEngine.Vector)"
    scope       = "required"
    licenses    = @([ordered]@{ license = [ordered]@{ id = "BSD-3-Clause" } })
    purl        = $CmapRef
    externalReferences = @([ordered]@{ type = "vcs"; url = "https://github.com/adobe-type-tools/cmap-resources" })
}
$CycloneDx.dependencies[0].dependsOn = @($CycloneDx.dependencies[0].dependsOn) + $CmapRef

foreach ($pkg in $ShippedPackages) {
    $purl = "pkg:nuget/$($pkg.Id)@$($pkg.Version)"
    $component = [ordered]@{
        type      = "library"
        "bom-ref" = $purl
        name      = $pkg.Id
        version   = $pkg.Version
        scope     = "required"
        licenses  = @([ordered]@{ expression = $pkg.License })
        purl      = $purl
    }
    if ($pkg.Project) { $component.externalReferences = @([ordered]@{ type = "website"; url = $pkg.Project }) }
    $CycloneDx.components = @($CycloneDx.components) + $component
    $CycloneDx.dependencies[0].dependsOn = @($CycloneDx.dependencies[0].dependsOn) + $purl
}

$CycloneDxPath = Join-Path $OutputDir "sbom.cyclonedx.json"
$CycloneDxJson = ConvertTo-Json -InputObject $CycloneDx -Depth 10
[System.IO.File]::WriteAllText($CycloneDxPath, $CycloneDxJson, [System.Text.Encoding]::UTF8)
Write-Host "  -> CycloneDX v1.6 SBOM generated: $CycloneDxPath" -ForegroundColor Green

# ---------------------------------------------------------------------
# 3. Generate SPDX v2.3 JSON
# ---------------------------------------------------------------------
$Spdx = [ordered]@{
    spdxVersion       = "SPDX-2.3"
    dataLicense       = "CC0-1.0"
    SPDXID            = "SPDXRef-DOCUMENT"
    name              = "PdfViewer-$Version"
    documentNamespace = "https://github.com/ramanacr/pdf-viewer/spdx/$Version/$DocGuid"
    creationInfo      = [ordered]@{
        creators = @(
            "Tool: PDF-Viewer-SBOM-Generator-3.0.0",
            "Organization: PDF Viewer Project"
        )
        created  = $Timestamp
    }
    packages          = @(
        [ordered]@{
            SPDXID           = "SPDXRef-Package-PdfViewer"
            name             = "PdfViewer"
            versionInfo      = $Version
            downloadLocation = "https://github.com/ramanacr/pdf-viewer/releases/tag/v$Version"
            filesAnalyzed    = $false
            licenseConcluded = "MIT"
            licenseDeclared  = "MIT"
            copyrightText    = "Copyright (c) 2026 PDF Viewer Project"
            description      = "Native Windows PDF Viewer with its own PDF engine (PdfEngine.Vector)"
            externalRefs     = @(
                [ordered]@{
                    referenceCategory = "PACKAGE-MANAGER"
                    referenceType     = "purl"
                    referenceLocator  = "pkg:github/ramanacr/pdf-viewer@$Version"
                }
            )
        }
    )
    relationships     = @(
        [ordered]@{
            spdxElementId      = "SPDXRef-DOCUMENT"
            relationshipType   = "DESCRIBES"
            relatedSpdxElement = "SPDXRef-Package-PdfViewer"
        }
    )
}

foreach ($fp in $FirstParty) {
    $id = "SPDXRef-Package-" + ($fp.Name -replace '[^A-Za-z0-9-]', '-')
    $Spdx.packages = @($Spdx.packages) + [ordered]@{
        SPDXID           = $id
        name             = $fp.Name
        versionInfo      = $Version
        downloadLocation = "https://github.com/ramanacr/pdf-viewer/tree/master/src/$($fp.Name)"
        filesAnalyzed    = $false
        licenseConcluded = "MIT"
        licenseDeclared  = "MIT"
        copyrightText    = "Copyright (c) 2026 PDF Viewer Project"
        description      = $fp.Description
    }
    $Spdx.relationships = @($Spdx.relationships) + [ordered]@{
        spdxElementId      = "SPDXRef-Package-PdfViewer"
        relationshipType   = "DEPENDS_ON"
        relatedSpdxElement = $id
    }
}

$Spdx.packages = @($Spdx.packages) + [ordered]@{
    SPDXID           = "SPDXRef-Package-Adobe-cmap-resources"
    name             = "cmap-resources"
    versionInfo      = $CmapCommit
    downloadLocation = "https://github.com/adobe-type-tools/cmap-resources"
    filesAnalyzed    = $false
    licenseConcluded = "BSD-3-Clause"
    licenseDeclared  = "BSD-3-Clause"
    copyrightText    = "Copyright 1990-2023 Adobe. All rights reserved."
    description      = "Adobe predefined CJK CMaps (PredefinedCMaps.zip, embedded in PdfEngine.Vector)"
}
$Spdx.relationships = @($Spdx.relationships) + [ordered]@{
    spdxElementId      = "SPDXRef-Package-PdfEngine-Vector"
    relationshipType   = "CONTAINS"
    relatedSpdxElement = "SPDXRef-Package-Adobe-cmap-resources"
}

foreach ($pkg in $ShippedPackages) {
    $id = "SPDXRef-Package-NuGet-" + ($pkg.Id -replace '[^A-Za-z0-9.-]', '-')
    $Spdx.packages = @($Spdx.packages) + [ordered]@{
        SPDXID           = $id
        name             = $pkg.Id
        versionInfo      = $pkg.Version
        downloadLocation = "https://www.nuget.org/packages/$($pkg.Id)/$($pkg.Version)"
        filesAnalyzed    = $false
        licenseConcluded = $pkg.License
        licenseDeclared  = $pkg.License
        copyrightText    = $(if ($pkg.Copyright) { $pkg.Copyright } else { "NOASSERTION" })
        externalRefs     = @([ordered]@{ referenceCategory = "PACKAGE-MANAGER"; referenceType = "purl"; referenceLocator = "pkg:nuget/$($pkg.Id)@$($pkg.Version)" })
    }
    $Spdx.relationships = @($Spdx.relationships) + [ordered]@{
        spdxElementId      = "SPDXRef-Package-PdfViewer"
        relationshipType   = "DEPENDS_ON"
        relatedSpdxElement = $id
    }
}

$SpdxPath = Join-Path $OutputDir "sbom.spdx.json"
$SpdxJson = ConvertTo-Json -InputObject $Spdx -Depth 10
[System.IO.File]::WriteAllText($SpdxPath, $SpdxJson, [System.Text.Encoding]::UTF8)
Write-Host "  -> SPDX v2.3 SBOM generated: $SpdxPath" -ForegroundColor Green

Write-Host "`nSBOM Generation Complete!" -ForegroundColor Cyan
