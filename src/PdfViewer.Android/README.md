# PdfViewer.Android: the engine on a phone (proof of concept)

An Android app that runs **the project's own C# PDF engine, `PdfEngine.Vector`, on the device**,
and draws pages with the same portable Skia renderer (`PdfEngine.Vector.Skia`) as the browser
demo. One pure-C# engine runs on Windows, in the browser and on Android.

Status: **proof of concept.** It builds, and it has been run on an Android 13 emulator (see
[what is verified](#what-is-verified)). It has not been run on a physical phone yet.

- **No permissions at all.** The manifest has no `INTERNET` permission and no storage
  permission. A document comes through the system document picker, which grants access to the
  one file the user picks. Backups are off (`android:allowBackup="false"`).
- **No telemetry.** No analytics, no crash reporting, no network code.
- **Document JavaScript never runs.** The engine has no script interpreter.
- Messages on screen are written for users or name an exception type. They never quote
  document text.

## Features

| Feature | Notes |
|---|---|
| Open a PDF | **Open** starts the system document picker (Storage Access Framework, `ACTION_OPEN_DOCUMENT`). Other apps can also hand the app a PDF ("Open with", `ACTION_VIEW` on a `content:` URI). Files up to 256 MB are read into memory. |
| Rendering | `SkiaVectorRenderer` on a worker thread, page by page, nearest the current page first, plus one screen above and below. |
| Scrolling | Vertical continuous scroll with fling. |
| Zoom | Pinch zoom around the fingers, double tap for 2.5× or back to fit width (0.5× to 8×). The old bitmap is stretched until the sharp one is drawn. |
| Page indicator | "3 / 24" in the toolbar. Tap it to go to a page. |
| Search | The engine's text extraction and search rules (the same as the desktop and the web demo). Highlights on the page, match count, previous and next. |
| Encrypted PDFs | Password prompt; a wrong password is refused. |
| Night pages | The engine's night mode: each page inverted after drawing. |
| Dark mode | The toolbar follows the system's dark theme, also when it switches while the app is open. |
| Rotation | The document stays open; the page at the top of the view stays there. |

## Architecture

```
 Android app (PdfViewer.Android, net9.0-android)
 ┌───────────────────────────────────────────────────────────────────────────┐
 │ MainActivity     toolbar, search bar, document picker, password dialog,   │
 │                  dark mode                                                │
 │ PageView         gestures (scroll, fling, pinch, double tap), render loop │
 │ SkiaView         a View that Skia draws into (Android bitmap pixels)      │
 ├───────────────────────────────────────────────────────────────────────────┤
 │ PdfViewer.Web.Engine (net9.0, no UI; shared with the browser demo)        │
 │   ViewerDocument        open, render, search, outline                     │
 │   ContinuousPageLayout  page column, zoom anchoring, visible pages        │
 │   PageImageCache<T>     rendered pages within a byte budget               │
 ├───────────────────────────────────────────────────────────────────────────┤
 │ PdfEngine.Vector        parser, content interpreter → display list, text  │
 │ PdfEngine.Vector.Skia   Skia replay of the display list                   │
 │ libSkiaSharp.so         SkiaSharp.NativeAssets.Android 3.116.1            │
 └───────────────────────────────────────────────────────────────────────────┘
```

1. **The same engine layer as the web demo.** `ViewerDocument` was written for the browser
   but uses no browser API, so the app uses it as it is. The layout and the image cache are new,
   platform-neutral classes in the same project, so the tests run them on Windows
   (`tests/PdfEngine.Vector.Skia.Tests/AndroidViewerTests.cs`).
2. **Threads.** Unlike the browser, Android has threads: `PdfPlatform.CanStartThreads` is true,
   the engine's text prefetcher runs, and rendering and search run with `Task.Run` off the UI
   thread. `ViewerDocument` still runs one engine operation at a time.
3. **Drawing.** The renderer writes BGRA pixels. Each page is copied once into an `SKImage`
   (native memory, not the Java heap) and kept in a 160 MB LRU cache. `SkiaView` draws the
   visible pages and the search highlights with Skia into the pixels of an Android bitmap, which
   is then put on the view's canvas. SkiaSharp's own `SKCanvasView` does the same; it is in a
   separate package (`SkiaSharp.Views`), which the app does not need for this.
4. **No MAUI.** Plain .NET for Android keeps the dependencies to the engine, SkiaSharp and its
   Android native library, and keeps the APK small.

### Fonts

Android keeps its fonts in `/system/fonts`, under other family names than Windows: Roboto, Noto
Serif, Droid Sans Mono, Noto Sans CJK, Noto Sans Symbols and others. On Android:

1. `PdfPlatform.InstalledFontDirectory` is `/system/fonts` (`OperatingSystem.IsAndroid()`).
2. `SystemFontCatalog.Installed` is `SystemFontCatalog.ForAndroid()`: the fonts of that folder,
   with **family aliases** (`SystemFontCatalog.AndroidFamilyAliases`). When substitution asks for
   a Windows family that is not there, the alias names the Android family that stands in:

   | Asked for | Drawn with |
   |---|---|
   | Arial, Helvetica, Segoe UI, Verdana, Tahoma, Calibri … | Roboto (then Noto Sans, Droid Sans) |
   | Times New Roman, Georgia, Cambria, Palatino … | Noto Serif (then Droid Serif) |
   | Courier New, Consolas | Droid Sans Mono (then Cutive Mono) |
   | Monotype Corsiva, Segoe Script | Dancing Script |
   | Microsoft YaHei, JhengHei, Yu Gothic, Malgun Gothic | Noto Sans CJK SC, TC, JP, KR |
   | Segoe UI Symbol, Cambria Math | Noto Sans Symbols 2, Noto Sans Math |

3. `SkiaFontSource.MatchFamily` uses the same aliases when Skia's font manager does not have
   the family, so the Unicode fallback chain of the Skia backend works too.

Each substituted glyph is still placed and fitted by the document's `/Widths`, as on Windows.
Windows is unchanged: its catalog has no aliases, and every new path is behind
`PdfPlatform.IsAndroid`. Tests simulate Android on Windows (`PlatformGuardTests`).

### Cryptography

.NET for Android implements `System.Security.Cryptography` with the platform's own providers, so
MD5, SHA-1, SHA-256, AES and RSA are all available. The engine's `PortableCrypto` uses the
platform implementation wherever it works, and keeps its managed MD5 and AES only for runtimes
without them (browser WebAssembly).

## Build and install

Prerequisites: the .NET 9 SDK, its `android` workload, the Android SDK with platform 35, and
JDK 17 or later.

```powershell
dotnet workload install android
dotnet build src/PdfViewer.Android -f net9.0-android -c Release `
  -p:AndroidSdkDirectory="<Android SDK>" -p:JavaSdkDirectory="<JDK>"
adb install -r src/PdfViewer.Android/bin/Release/net9.0-android/io.github.ramanacr.pdfviewer-Signed.apk
```

- **SDK 9.** If SDK 10 is also installed, pin SDK 9 (a `global.json` that you do not commit), so
  that the net9.0-android workload is used. CI does this too.
- **Signing.** The build signs the APK with the .NET SDK's per-user debug key, which is outside
  the repository. That is what `adb install` needs. The repository holds no keystore. A store
  release would need its own key.
- **Windows path length.** The Release build compiles the startup code ahead of time (Mono AOT).
  From a very deep checkout path, that step fails with `Precompiling failed … condition 'corlib'
  not met`. Build from a short path (for example `subst P: <checkout>`).
- **CI.** `.github/workflows/android.yml` builds the APK on Ubuntu, checks that the manifest
  asks for no permission, and uploads the APK as the `pdf-viewer-android-apk` artifact. It is a
  separate workflow that runs only when the app or the code it is built from changes, so the
  main CI job is not slowed down.

### Size

The Release APK holds two ABIs, `arm64-v8a` (phones) and `x86_64` (emulators): **24.1 MB**
(23.0 MiB). The largest parts, per ABI: `libSkiaSharp.so` about 9 MB, the .NET assemblies about
5.2 MB, the Mono runtime about 3.2 MB, ahead-of-time code for `System.Private.CoreLib` 1.7 MB. An
arm64-only APK would be about half that size.

## What is verified

On the development machine (Windows 11), with the existing Android 13 emulator image (Pixel 3a,
API 33, x86_64), the Release APK was installed with `adb` and driven with `adb shell input`:

| Checked | Result |
|---|---|
| Install and start | Starts, shows the empty state. |
| Document picker | **Open** shows the system picker; the chosen file opens. |
| Rendering and fonts | A 3-page test PDF with non-embedded Helvetica, Helvetica-Bold, Times-Roman and Courier draws all text: Roboto, Noto Serif and Droid Sans Mono, fitted to the widths. Shapes and colours are correct. |
| Encrypted PDF | The AES-128 fixture (`tests/PdfViewer.Tests/Fixtures/encrypted.pdf`) asks for its password, refuses a wrong one, opens with the right one and renders. |
| Search | "android" finds 6 matches on 3 pages; highlights sit on the words; next match works. |
| Scrolling, double-tap zoom | Swipe scrolls and the page indicator follows; double tap zooms in and the page is drawn again sharp. |
| Night pages | Pages are inverted. |
| Dark mode, rotation | Switching the system to dark mode, and rotating to landscape, keep the document open and redraw it. |
| Permissions | The merged manifest has no `uses-permission` element (also checked by CI). |

The emulator run found two bugs that were fixed: the soft keyboard service was asked for under
the wrong name (`InputService` instead of `InputMethodService`), which crashed the search bar,
and SELinux logged a denied file lock for every font read, now avoided with
`System.IO.DisableFileLocking`.

## What is not verified

1. **A physical phone.** Only the x86_64 emulator was used. The arm64 code is built but has not
   run.
2. **Pinch zoom.** `adb` cannot send a two-finger gesture. The pinch code uses Android's
   `ScaleGestureDetector`; the zoom-anchoring math is unit-tested.
3. **"Open with" (`ACTION_VIEW`)** from another app, and the go-to-page dialog.
4. **Android 14 and 15.** On Android 15 apps are drawn edge to edge; the layout uses
   `fitsSystemWindows`, which should keep the toolbar below the status bar, but it was not seen.
5. **The last two small changes**: the status-bar icon colour after a dark-mode switch and
   selecting the old query when the search bar opens. They were built, not run.
6. **Performance and memory** on real documents: large pages, many images, the first scan of
   `/system/fonts` (about 200 files on the emulator), CJK text.
7. **Certificate-encrypted PDFs.** RSA is available on Android, but the app has no certificate
   store and was not tested with such a file.

## Known gaps

1. **Fonts.** Only `.ttf` and `.ttc` files are catalogued, so `.otf` fonts are not used. Roboto
   on Android 12 and later is one variable font: bold is simulated (emboldened), not the real
   bold instance. Emoji fonts are bitmaps and are not used.
2. **Rendering** has the Skia backend's gaps (see its README): CMYK and YCCK JPEG images are not
   decoded, and small text has slightly less ink than Direct2D draws.
3. **Whole-page bitmaps.** Each page is one bitmap, at most 3000 × 3000 pixels, so very deep zoom
   gets blurry. Tiles would fix this.
4. **Not in the app:** outline (bookmarks), thumbnails, text selection and copy, annotations,
   forms, printing, sharing, recent files.
5. **The activity keeps the document** across rotation and theme changes, but not if Android
   ends the process in the background. Reopen the file then.

## Tests

- `tests/PdfEngine.Vector.Tests/PlatformGuardTests.cs`: Android simulated on Windows (font folder,
  alias catalog, threads), family aliases in `Match`, `FindFamily` and `Fallbacks`, and the
  alias table covering every family that substitution asks for.
- `tests/PdfEngine.Vector.Skia.Tests/AndroidViewerTests.cs`: the page layout (fit width, visible
  and current pages, zoom anchoring, rotation, search rectangles), the image cache, and
  non-embedded text drawn with an aliased stand-in through `ViewerDocument`.
