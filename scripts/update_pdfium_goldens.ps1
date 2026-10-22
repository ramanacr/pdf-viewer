<#
.SYNOPSIS
    Regenerates the PDFium goldens of the viewer tests (tests/PdfViewer.Tests/PdfiumGoldens).

.DESCRIPTION
    The PDFium-free build checks the vector engine against values PDFium gave for the tests'
    synthetic documents, committed as small JSON files and PNG images. The developer build
    (PdfiumRenderer=true, the default) checks those files against PDFium's live results on every
    run and fails when they drift. Run this script after a deliberate change (a PDFium upgrade, a
    changed fixture), then review the diff before committing it: the goldens are the oracle.

    With -Check it regenerates nothing and only runs the drift check.
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\PdfViewer.Tests\PdfViewer.Tests.csproj'

if (-not $Check) { $env:PDFIUM_GOLDENS_UPDATE = '1' }
try {
    dotnet test $project -nologo -p:PdfiumRenderer=true --filter 'Category=PdfiumGolden'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item Env:\PDFIUM_GOLDENS_UPDATE -ErrorAction SilentlyContinue
}

if (-not $Check) {
    # A second run without the variable proves the files just written are what PDFium gives.
    dotnet test $project -nologo -p:PdfiumRenderer=true --filter 'Category=PdfiumGolden'
    exit $LASTEXITCODE
}
