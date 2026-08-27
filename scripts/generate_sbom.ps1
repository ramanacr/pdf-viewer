# =====================================================================
# Software Bill of Materials (SBOM) Generator for PDF Viewer
# Generates:
#   - sbom.cyclonedx.json (CycloneDX v1.6 Specification)
#   - sbom.spdx.json       (SPDX v2.3 Specification)
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

# 1. Native PDFium Checksum & Info
$PdfiumDllPath = "$RootDir\src\PdfViewer\runtimes\win-x64\native\pdfium.dll"
$PdfiumSha256 = "ADAC8CE034015427B5DAA81F8EEDDFCC8E84BC2A9F036F007890FF18BD4388C4"
if (Test-Path $PdfiumDllPath) {
    $PdfiumSha256 = (Get-FileHash -Path $PdfiumDllPath -Algorithm SHA256).Hash.ToUpper()
}

$PdfiumVersion = "154.0.8021.0"
$PdfiumTag = "chromium/8021"

# ---------------------------------------------------------------------
# 2. Generate CycloneDX v1.6 JSON
# ---------------------------------------------------------------------
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
                    version = "2.0.0"
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
            description = "High-performance native Windows PDF Viewer powered by Google PDFium"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id  = "MIT"
                        url = "https://opensource.org/licenses/MIT"
                    }
                }
            )
            purl        = "pkg:github/ramanacr/pdf-viewer@$Version"
        }
    }
    components   = @(
        # Managed Dependency: CommunityToolkit.Mvvm
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:nuget/CommunityToolkit.Mvvm@8.4.0"
            name        = "CommunityToolkit.Mvvm"
            version     = "8.4.0"
            description = "Official modern MVVM toolkit library for .NET with observable properties and commands"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id  = "MIT"
                        url = "https://licenses.nuget.org/MIT"
                    }
                }
            )
            purl        = "pkg:nuget/CommunityToolkit.Mvvm@8.4.0"
            externalReferences = @(
                [ordered]@{
                    type = "vcs"
                    url  = "https://github.com/CommunityToolkit/dotnet"
                }
            )
        },
        # Native Engine: Google PDFium
        [ordered]@{
            type        = "file"
            "bom-ref"   = "pkg:generic/google/pdfium@$PdfiumVersion?arch=x86_64&os=windows"
            name        = "pdfium.dll"
            version     = $PdfiumVersion
            description = "Google PDFium standalone native PDF rendering and search engine (pinned binary $PdfiumTag)"
            scope       = "required"
            hashes      = @(
                [ordered]@{
                    alg     = "SHA-256"
                    content = $PdfiumSha256
                }
            )
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id  = "BSD-3-Clause"
                        url = "https://pdfium.googlesource.com/pdfium/+/refs/heads/main/LICENSE"
                    }
                }
            )
            purl        = "pkg:generic/google/pdfium@$PdfiumVersion?arch=x86_64&os=windows"
            externalReferences = @(
                [ordered]@{
                    type = "vcs"
                    url  = "https://pdfium.googlesource.com/pdfium"
                },
                [ordered]@{
                    type = "distribution"
                    url  = "https://github.com/bblanchon/pdfium-binaries/releases/tag/$PdfiumTag"
                }
            )
        },
        # Embedded Subcomponent: FreeType
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/freetype/freetype"
            name        = "FreeType"
            description = "Font rasterization engine used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "FTL"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "https://freetype.org/"
                }
            )
        },
        # Embedded Subcomponent: libjpeg-turbo
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/libjpeg-turbo/libjpeg-turbo"
            name        = "libjpeg-turbo"
            description = "High-speed JPEG image codec used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "IJG"
                    }
                },
                [ordered]@{
                    license = [ordered]@{
                        id = "BSD-3-Clause"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "https://libjpeg-turbo.org/"
                }
            )
        },
        # Embedded Subcomponent: libpng
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/libpng/libpng"
            name        = "libpng"
            description = "PNG reference library used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "libpng-2.0"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "http://www.libpng.org/pub/png/libpng.html"
                }
            )
        },
        # Embedded Subcomponent: zlib
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/zlib/zlib"
            name        = "zlib"
            description = "General purpose data compression library used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "Zlib"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "https://zlib.net/"
                }
            )
        },
        # Embedded Subcomponent: OpenJPEG
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/openjpeg/openjpeg"
            name        = "OpenJPEG"
            description = "Open-source JPEG 2000 codec used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "BSD-2-Clause"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "https://www.openjpeg.org/"
                }
            )
        },
        # Embedded Subcomponent: Little CMS (lcms2)
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/littlecms/lcms"
            name        = "Little-CMS"
            description = "Color management engine used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "MIT"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "https://www.littlecms.com/"
                }
            )
        },
        # Embedded Subcomponent: ICU
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/unicode/icu"
            name        = "ICU"
            description = "International Components for Unicode used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "Unicode-DFS-2016"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "website"
                    url  = "https://icu.unicode.org/"
                }
            )
        },
        # Embedded Subcomponent: abseil-cpp
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/abseil/abseil-cpp"
            name        = "abseil-cpp"
            description = "Abseil C++ common libraries used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "Apache-2.0"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "vcs"
                    url  = "https://github.com/abseil/abseil-cpp"
                }
            )
        },
        # Embedded Subcomponent: Anti-Grain Geometry (AGG)
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/agg/agg23"
            name        = "agg23"
            description = "Anti-Grain Geometry 2D vector graphics library used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        name = "Anti-Grain Geometry Public License"
                    }
                }
            )
        },
        # Embedded Subcomponent: fast_float
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/fastfloat/fast_float"
            name        = "fast_float"
            description = "Fast float parsing library used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "Apache-2.0"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "vcs"
                    url  = "https://github.com/fastfloat/fast_float"
                }
            )
        },
        # Embedded Subcomponent: simdutf
        [ordered]@{
            type        = "library"
            "bom-ref"   = "pkg:generic/simdutf/simdutf"
            name        = "simdutf"
            description = "SIMD-accelerated UTF validation and transcoding used inside Google PDFium"
            scope       = "required"
            licenses    = @(
                [ordered]@{
                    license = [ordered]@{
                        id = "Apache-2.0"
                    }
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = "vcs"
                    url  = "https://github.com/simdutf/simdutf"
                }
            )
        }
    )
    dependencies = @(
        [ordered]@{
            ref       = "PdfViewer@$Version"
            dependsOn = @(
                "pkg:nuget/CommunityToolkit.Mvvm@8.4.0",
                "pkg:generic/google/pdfium@$PdfiumVersion?arch=x86_64&os=windows"
            )
        },
        [ordered]@{
            ref       = "pkg:generic/google/pdfium@$PdfiumVersion?arch=x86_64&os=windows"
            dependsOn = @(
                "pkg:generic/freetype/freetype",
                "pkg:generic/libjpeg-turbo/libjpeg-turbo",
                "pkg:generic/libpng/libpng",
                "pkg:generic/zlib/zlib",
                "pkg:generic/openjpeg/openjpeg",
                "pkg:generic/littlecms/lcms",
                "pkg:generic/unicode/icu",
                "pkg:generic/abseil/abseil-cpp",
                "pkg:generic/agg/agg23",
                "pkg:generic/fastfloat/fast_float",
                "pkg:generic/simdutf/simdutf"
            )
        }
    )
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
            "Tool: PDF-Viewer-SBOM-Generator-2.0.0",
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
            description      = "Native Windows PDF Viewer powered by Google PDFium"
            externalRefs     = @(
                [ordered]@{
                    referenceCategory = "PACKAGE-MANAGER"
                    referenceType     = "purl"
                    referenceLocator  = "pkg:github/ramanacr/pdf-viewer@$Version"
                }
            )
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-CommunityToolkit-Mvvm"
            name             = "CommunityToolkit.Mvvm"
            versionInfo      = "8.4.0"
            downloadLocation = "https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.0"
            filesAnalyzed    = $false
            licenseConcluded = "MIT"
            licenseDeclared  = "MIT"
            copyrightText    = "Copyright (c) .NET Foundation and Contributors"
            externalRefs     = @(
                [ordered]@{
                    referenceCategory = "PACKAGE-MANAGER"
                    referenceType     = "purl"
                    referenceLocator  = "pkg:nuget/CommunityToolkit.Mvvm@8.4.0"
                }
            )
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-Google-PDFium"
            name             = "pdfium.dll"
            versionInfo      = $PdfiumVersion
            downloadLocation = "https://github.com/bblanchon/pdfium-binaries/releases/tag/$PdfiumTag"
            filesAnalyzed    = $false
            licenseConcluded = "BSD-3-Clause"
            licenseDeclared  = "BSD-3-Clause"
            copyrightText    = "Copyright 2014 PDFium Authors. All rights reserved."
            checksums        = @(
                [ordered]@{
                    algorithm     = "SHA256"
                    checksumValue = $PdfiumSha256
                }
            )
            externalRefs     = @(
                [ordered]@{
                    referenceCategory = "PACKAGE-MANAGER"
                    referenceType     = "purl"
                    referenceLocator  = "pkg:generic/google/pdfium@$PdfiumVersion?arch=x86_64&os=windows"
                }
            )
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-FreeType"
            name             = "FreeType"
            downloadLocation = "https://freetype.org/"
            filesAnalyzed    = $false
            licenseConcluded = "FTL"
            licenseDeclared  = "FTL"
            copyrightText    = "Copyright 1996-2024 by David Turner, Robert Wilhelm, and Werner Lemberg"
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-LibJpegTurbo"
            name             = "libjpeg-turbo"
            downloadLocation = "https://libjpeg-turbo.org/"
            filesAnalyzed    = $false
            licenseConcluded = "IJG AND BSD-3-Clause"
            licenseDeclared  = "IJG AND BSD-3-Clause"
            copyrightText    = "Copyright (C) 2009-2024 D. R. Commander. All Rights Reserved."
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-LibPng"
            name             = "libpng"
            downloadLocation = "http://www.libpng.org/pub/png/libpng.html"
            filesAnalyzed    = $false
            licenseConcluded = "libpng-2.0"
            licenseDeclared  = "libpng-2.0"
            copyrightText    = "Copyright (c) 1995-2024 The PNG Reference Library Authors"
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-Zlib"
            name             = "zlib"
            downloadLocation = "https://zlib.net/"
            filesAnalyzed    = $false
            licenseConcluded = "Zlib"
            licenseDeclared  = "Zlib"
            copyrightText    = "Copyright (C) 1995-2024 Jean-loup Gailly and Mark Adler"
        },
        [ordered]@{
            SPDXID           = "SPDXRef-Package-LittleCMS"
            name             = "Little-CMS"
            downloadLocation = "https://www.littlecms.com/"
            filesAnalyzed    = $false
            licenseConcluded = "MIT"
            licenseDeclared  = "MIT"
            copyrightText    = "Copyright (c) 1998-2024 Marti Maria Saguer"
        }
    )
    relationships     = @(
        [ordered]@{
            spdxElementId      = "SPDXRef-DOCUMENT"
            relationshipType   = "DESCRIBES"
            relatedSpdxElement = "SPDXRef-Package-PdfViewer"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-PdfViewer"
            relationshipType   = "DEPENDS_ON"
            relatedSpdxElement = "SPDXRef-Package-CommunityToolkit-Mvvm"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-PdfViewer"
            relationshipType   = "DEPENDS_ON"
            relatedSpdxElement = "SPDXRef-Package-Google-PDFium"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-Google-PDFium"
            relationshipType   = "CONTAINS"
            relatedSpdxElement = "SPDXRef-Package-FreeType"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-Google-PDFium"
            relationshipType   = "CONTAINS"
            relatedSpdxElement = "SPDXRef-Package-LibJpegTurbo"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-Google-PDFium"
            relationshipType   = "CONTAINS"
            relatedSpdxElement = "SPDXRef-Package-LibPng"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-Google-PDFium"
            relationshipType   = "CONTAINS"
            relatedSpdxElement = "SPDXRef-Package-Zlib"
        },
        [ordered]@{
            spdxElementId      = "SPDXRef-Package-Google-PDFium"
            relationshipType   = "CONTAINS"
            relatedSpdxElement = "SPDXRef-Package-LittleCMS"
        }
    )
}

$SpdxPath = Join-Path $OutputDir "sbom.spdx.json"
$SpdxJson = ConvertTo-Json -InputObject $Spdx -Depth 10
[System.IO.File]::WriteAllText($SpdxPath, $SpdxJson, [System.Text.Encoding]::UTF8)
Write-Host "  -> SPDX v2.3 SBOM generated: $SpdxPath" -ForegroundColor Green

Write-Host "`nSBOM Generation Complete!" -ForegroundColor Cyan
