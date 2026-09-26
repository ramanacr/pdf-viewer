<#
.SYNOPSIS
    Gate for the nightly corpus run: compares a `vectorpdf corpus --summary` result with the
    committed baseline and fails on regressions.

.DESCRIPTION
    Fails when, against the baseline:
      - fewer files open in the vector engine, or more fail where PDFium opened them;
      - untyped errors appear;
      - the share of fully vector pages drops by more than -RatioTolerance (default 0.002),
        or the vector share of page area by more than half of it;
      - the worst pixel difference grows by more than -DiffTolerance (default 1.0 / 255).
    New or grown fallback reasons are reported, not failed: a reason can move when a document
    is handled better elsewhere. Improvements are reported too, so the baseline can be raised.
    Writes a Markdown table to $env:GITHUB_STEP_SUMMARY when set, and always to the console.

.EXAMPLE
    pwsh eng/vectorpdf/corpus/compare-summary.ps1 -Baseline eng/vectorpdf/baseline-corpus-summary.warp.json -Current summary.json
#>
param(
    [Parameter(Mandatory)] [string]$Baseline,
    [Parameter(Mandatory)] [string]$Current,
    [double]$RatioTolerance = 0.002,
    [double]$DiffTolerance = 1.0
)

$ErrorActionPreference = 'Stop'
$base = Get-Content $Baseline -Raw | ConvertFrom-Json
$cur = Get-Content $Current -Raw | ConvertFrom-Json

function WorstDiff($summary) {
    # Entries read "13.4 path p1": the leading number is the mean channel difference.
    $first = @($summary.worstPixelDiffs) | Select-Object -First 1
    if (-not $first) { return 0.0 }
    return [double]::Parse(($first -split ' ')[0], [System.Globalization.CultureInfo]::InvariantCulture)
}

$failures = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()
$rows = [System.Collections.Generic.List[string]]::new()

function Row($name, $b, $c, [string]$verdict) {
    $rows.Add("| $name | $b | $c | $verdict |")
}

function Check-AtLeast($name, $b, $c) {
    if ($c -lt $b) { $failures.Add("$name fell from $b to $c"); Row $name $b $c 'regressed' }
    elseif ($c -gt $b) { $notes.Add("$name rose from $b to $c"); Row $name $b $c 'improved' }
    else { Row $name $b $c 'same' }
}

function Check-AtMost($name, $b, $c) {
    if ($c -gt $b) { $failures.Add("$name rose from $b to $c"); Row $name $b $c 'regressed' }
    elseif ($c -lt $b) { $notes.Add("$name fell from $b to $c"); Row $name $b $c 'improved' }
    else { Row $name $b $c 'same' }
}

if ($cur.files -ne $base.files) {
    $notes.Add("corpus size changed from $($base.files) to $($cur.files) files: refresh the baseline")
}
Row 'files' $base.files $cur.files ($(if ($cur.files -eq $base.files) { 'same' } else { 'changed' }))
Check-AtLeast 'vectorOpen' $base.vectorOpen $cur.vectorOpen
Check-AtMost 'vectorFailedWherePdfiumOpened' $base.vectorFailedWherePdfiumOpened $cur.vectorFailedWherePdfiumOpened
Check-AtMost 'untypedErrors' $base.untypedErrors $cur.untypedErrors

foreach ($ratio in @(@{ Name = 'pagesFullyVectorRatio'; Tol = $RatioTolerance }, @{ Name = 'vectorAreaRatio'; Tol = $RatioTolerance / 2 })) {
    $b = [double]$base.($ratio.Name); $c = [double]$cur.($ratio.Name)
    if ($c -lt $b - $ratio.Tol) { $failures.Add("$($ratio.Name) fell from $b to $c"); Row $ratio.Name $b $c 'regressed' }
    elseif ($c -gt $b + 1e-9) { $notes.Add("$($ratio.Name) rose from $b to $c"); Row $ratio.Name $b $c 'improved' }
    else { Row $ratio.Name $b $c 'within tolerance' }
}

$bw = WorstDiff $base; $cw = WorstDiff $cur
if ($cw -gt $bw + $DiffTolerance) { $failures.Add("worst pixel difference grew from $bw to $cw"); Row 'worst pixel diff' $bw $cw 'regressed' }
else { Row 'worst pixel diff' $bw $cw 'within tolerance' }

$baseReasons = @{}
if ($base.fallbackReasons) { $base.fallbackReasons.PSObject.Properties | ForEach-Object { $baseReasons[$_.Name] = [int]$_.Value } }
if ($cur.fallbackReasons) {
    foreach ($p in $cur.fallbackReasons.PSObject.Properties) {
        $was = if ($baseReasons.ContainsKey($p.Name)) { $baseReasons[$p.Name] } else { 0 }
        if ([int]$p.Value -gt $was) { $notes.Add("fallback reason $($p.Name): $was -> $($p.Value)") }
    }
}

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine('## Vector corpus against baseline')
[void]$md.AppendLine()
[void]$md.AppendLine('| Measure | Baseline | This run | Verdict |')
[void]$md.AppendLine('|---|---|---|---|')
foreach ($r in $rows) { [void]$md.AppendLine($r) }
if ($notes.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('**Notes**')
    foreach ($n in $notes) { [void]$md.AppendLine("- $n") }
}
[void]$md.AppendLine()
[void]$md.AppendLine($(if ($failures.Count -eq 0) { '**Result: no regression.**' } else { "**Result: $($failures.Count) regression(s).**" }))
foreach ($f in $failures) { [void]$md.AppendLine("- $f") }

$text = $md.ToString()
Write-Host $text
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $text }
exit $(if ($failures.Count -eq 0) { 0 } else { 1 })
