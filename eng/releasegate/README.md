# Release gates

Two checks that hold a release to what the last one shipped, each against a baseline committed
here, and one that holds it to having no PDFium. The first two run in CI
(`.github/workflows/ci.yml`) on the payload that `scripts/build_publish.ps1 -DryRun` stages in
`publish/app`, and the payload gate also runs at the end of a full `scripts/build_publish.ps1`,
against the installer it has just built. The PDFium-free gate runs inside every
`scripts/build_publish.ps1`, dry run or full, so CI and the release workflow both run it.

Nothing here touches the network or collects anything: the measurements are local files.

| gate | baseline | fails when | tool |
|---|---|---|---|
| Installer payload size | `baseline-payload.json` | the payload, the payload compressed, or the installer grows more than **5 %**, or passes a hard **budget** | `releasegate payload` |
| Cold start | `baseline-startup.json` | the **warm** median start to first page exceeds the baseline by more than the tolerance | `releasegate startup` |
| PDFium-free | none | the payload holds a `pdfium*.dll` or `PdfEngine.Pdfium` (loose, bundled, or named by a `.deps.json`), or the app, started with `PDF_PDFIUM_DISABLED=1`, does not open, draw and search the probe document without loading PDFium | `releasegate pdfium-free` |

`releasegate` is `ReleaseGate.Tool` in this folder. From the repository root:

```powershell
dotnet build eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release
$gate = 'dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build --'
```

Exit codes: `0` passed, `1` the gate failed, `2` the measurement itself failed (no payload, the
application would not start, a probe timed out).

## Installer payload size

```powershell
./scripts/build_publish.ps1 -DryRun
dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build -- `
    payload --dir publish/app --baseline eng/releasegate/baseline-payload.json --check
```

What is measured:

- every file in the payload, by path (`files`), and their total (`totalBytes`);
- the payload zipped at optimal compression, as `Payload.zip` is (`compressedBytes`). The
  installer embeds that zip, so this is what `PdfViewerSetup.exe` follows; a dry run builds no
  installer, so this is the number CI gates the installer on;
- `PdfViewerSetup.exe` itself, when `--installer` is given (the full release build does);
- what is inside each single-file executable (`bundles`): `PdfViewer.exe` is nearly the whole
  payload, so the manifest the .NET host writes into it is read and every bundled assembly and
  native library is listed with the bytes it occupies.

Tolerance, in `baseline-payload.json` under `tolerance`:

- `relative` (0.05): each of the three sizes may grow up to 5 % over the baseline;
- `budgetBytes` (50 MiB) and `compressedBudgetBytes` (19 MiB): hard ceilings. A refresh cannot
  raise them - `--write-baseline` refuses a payload over budget - so passing one is a deliberate,
  reviewed edit of this file.

A failure lists the files added and removed (loose and bundled), the largest growth, and the
largest files, inside the executable as well as beside it:

```
- FAIL: payload grew +6.6 %, from 43.61 MiB to 46.47 MiB (limit 45.79 MiB, +5 %)
Added files (1, 2.86 MiB):
  +     2.86 MiB  runtimes-stray.dll
Largest files (10 files, 46.47 MiB in total):
     43.55 MiB  PdfViewer.exe
     ...
Largest inside PdfViewer.exe (22 bundled files):
     23.73 MiB  Microsoft.Windows.SDK.NET.dll
      6.93 MiB  pdfium.dll
      6.93 MiB  runtimes/win-x64/native/pdfium.dll
     ...
```

(That last listing shows a real finding this gate made: `pdfium.dll` was bundled twice, because
the project files published it to two target paths. It was then deployed once, which took 6.93
MiB off the payload and about 3.5 MiB off the installer. Since M10 the published app has no
PDFium at all: the 2026-10-18 baseline is 31.46 MiB, 9.64 MiB compressed, installer 9.89 MiB,
against 38.55 MiB, 13.24 MiB compressed for the same source published with PDFium.)

A shrink of more than the tolerance is reported as a note asking for a refresh, so the gate
holds the gain.

## Cold start

```powershell
./scripts/build_publish.ps1 -DryRun
dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build -- `
    startup --app publish/app --environment "developer workstation" --baseline eng/releasegate/baseline-startup.json --check
```

### What is measured

The application has a probe switch, `PdfViewer.exe --startup-probe <result.json> <document.pdf>`
(`src/PdfViewer/Services/StartupProbe.cs`). It opens the document as a double-click would,
records each milestone in milliseconds since the operating system created the process, writes
them to the result file and exits. It skips the first-run privacy question and the update check
(it must not wait on a person or touch the network) and keeps its settings beside the result, so
a measurement never changes the user's recent files. Without the switch none of it runs.

| milestone | meaning |
|---|---|
| `appStartup` | `Application.OnStartup` entered: runtime, host and WPF are up |
| `windowLoaded` | the main window's `Loaded` |
| `documentOpenStart` | the document is handed to the shell |
| `windowShown` | the window's first frame (`ContentRendered`); absent when page 1 is already in that frame |
| `firstPageSurface` | page 1 has a vector surface or bitmap |
| `firstPageRendered` | the frame drawing page 1 has been composed and handed to the compositor - **the gated metric** |

The document is generated by the tool (`ProbeDocument.cs`), byte-for-byte the same everywhere:
twelve Letter pages of heading, body text in two standard fonts, a filled and stroked bar
chart, a Bezier line and an RGB image. `releasegate probe-pdf out.pdf` writes it for a manual run.

`releasegate startup`:

1. **Cold**, `--cold` launches (default 3): each is a fresh install - the payload copied to a new
   folder, with a new single-file extraction directory (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`), so the
   host unpacks its native libraries again exactly as on a user's first launch.
2. **Warm**, one install launched `--warmup` times (default 2, discarded: they fill the
   extraction directory and the caches) and then `--runs` times (default 10), measured.

It reports median and p95 (nearest rank: always a time that was measured; with ten runs, p95 is
the slowest) for both, plus the median of every milestone, so a regression can be placed in
the runtime, the window or the document. `PDF_ENGINE_MODE` is cleared for the launches: the
shipped default engine is what is measured.

### Tolerance

In `baseline-startup.json` under `tolerance`: the limit is
`baseline warm median x (1 + relative) + absoluteMs`.

- Same environment as the baseline (`--environment` matches the baseline's): **+35 % + 250 ms**.
  On the workstation the baseline was recorded on, heavy background load (parallel builds) moved the warm median from 4.6 s to 6.2 s, +35 %; this sits just past that.
- A different environment: **+100 % + 500 ms** (`crossEnvironmentRelative`,
  `crossEnvironmentAbsoluteMs`). The hardware differs, so only a gross regression - a start
  that doubles - is evidence. The committed baseline is from a developer workstation and CI
  labels itself `github windows-latest`, so CI uses this until a CI baseline is recorded (below).

Only the warm median is gated. Cold starts are reported against the same rule but never fail:
emptying the operating system's file cache needs administrator rights, so on a shared runner a
"cold" start is too variable to block a merge on. p95 values are reported, not gated.

## PDFium-free

```powershell
./scripts/build_publish.ps1 -DryRun
dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build -- `
    pdfium-free --app publish/app --out pdfium-free.json
```

The published app is built with `-p:PdfiumRenderer=false` (M10), which leaves PdfEngine.Pdfium and
`pdfium.dll` out. This gate checks that the payload really is PDFium-free, and that the app works
without it:

1. **Files.** Every file in the payload, and every file bundled inside a single-file executable
   (the manifest `BundleManifest` reads), is checked: no `pdfium*.dll`, no `PdfEngine.Pdfium*`,
   and no `.deps.json` that names PdfEngine.Pdfium. Files the host extracts during the launch
   below are checked too.
2. **Launch.** A copy of the payload is started as
   `PdfViewer.exe --startup-probe <result.json> <probe.pdf> --search "Startup probe"` with
   `PDF_PDFIUM_DISABLED=1` (and `PDF_ENGINE_MODE` cleared). The probe opens the twelve-page probe
   document, waits for page 1 on screen, runs the viewer's own search, and writes the matches
   with what of PDFium is in the process: the managed adapter among the loaded assemblies, the
   native library among the loaded modules, and whether the PDFium renderer was compiled in. The
   gate passes when page 1 was drawn, the heading was found on all twelve pages, PDFium was
   disabled, nothing of it was loaded, and the build has no PDFium renderer.

`--no-launch` checks the files only. A failure names each PDFium file, or the reason the launch
did not pass; `--out` keeps the probe's result.

## Refreshing a baseline

Refresh when a change moves a number on purpose (a new dependency, a faster start), in the same
commit as that change, and say why in the commit message. `--write-baseline` writes the new
measurement and keeps the file's `tolerance` (and, for the payload, the `note` and the last
measured installer size when the new run built none).

**Payload** - from a dry-run publish, or from a full build to update the installer size too:

```powershell
./scripts/build_publish.ps1 -DryRun
dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build -- `
    payload --dir publish/app --baseline eng/releasegate/baseline-payload.json --write-baseline
# with an installer: add  --installer <path to PdfViewerSetup.exe>
```

**Startup** - on a quiet machine (close builds, browsers and other heavy work first), with the
same `--environment` label every time you refresh on that machine:

```powershell
./scripts/build_publish.ps1 -DryRun
dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build -- `
    startup --app publish/app --environment "developer workstation" --runs 15 `
    --baseline eng/releasegate/baseline-startup.json --write-baseline
```

**Startup, from CI** - to hold CI to the tight tolerance, record the baseline from a CI run on
`main`: download the `release-gates` artifact and write it as the baseline (no re-measuring;
`--from` reads a saved result):

```powershell
dotnet run --project eng/releasegate/ReleaseGate.Tool/ReleaseGate.Tool.csproj -c Release --no-build -- `
    startup --from startup-timings.json --baseline eng/releasegate/baseline-startup.json --write-baseline
```

The same `--from` works for `payload` with `payload-size.json`.

A changed probe document (`ProbeDocument.cs`) changes `documentSha256`; the gate then notes that
the baseline is stale, and the startup baseline must be re-recorded.
