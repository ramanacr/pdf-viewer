# PdfViewer.Android: the engine on a phone

An Android app that runs **the project's own C# PDF engine, `PdfEngine.Vector`, on the device**,
and draws pages with the same portable Skia renderer (`PdfEngine.Vector.Skia`) as the browser
demo. One pure-C# engine runs on Windows, in the browser and on Android.

Status: **phase 2 of the reader** (a preview, prepared for Google Play: docs/android-release.md): phase 1 reads, phase 2 fills forms
and annotates. It builds, and it has been run on an Android 13 emulator (see
[what is verified](#what-is-verified)). It has not been run on a physical phone yet.

- **No permissions at all.** The manifest has no `INTERNET` permission and no storage
  permission. A document comes through the system document picker, which grants access to the
  one file the user picks; the app keeps that grant for its recent list. A copy with edits is
  written through the system's save dialog, which grants access to the one file the user names.
  Backups are off (`android:allowBackup="false"`).
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
| Zoom | Pinch zoom around the fingers, double tap for 2.5× or back to fit width (0.5× to 8×). A keyboard's + and − zoom by steps, 0 fits the width. |
| Deep zoom | Each page has one bitmap of at most 2.4 megapixels. At a deeper zoom, the part of the page in view is drawn over it in 512 × 512 tiles at the zoom's own scale (`PageTiles`), nearest the centre first, so 8× stays sharp. Tiles are kept in a 48 MB LRU cache (`TileCache`), bitmaps in a 96 MB one; while a pinch runs, the last tiles are stretched. |
| Outline | **⋮ → Outline**: the document's bookmarks as an indented list; tap one to go to its page. |
| Pages | **⋮ → Pages**: a grid of page thumbnails, the current page marked; tap one to go to it. |
| Text selection | Long press selects the word under the finger; two handles extend the selection (on one page); **Copy** puts it on the clipboard, **All** takes the whole page. The rules are the desktop viewer's (`ViewerTextLayout`): lines, words, selection bands, line-end hyphens removed. A document that does not allow copying (its permissions, ISO 32000-2 Table 22) selects nothing and says so. |
| Share | **⋮ → Share**: the system share sheet with the document's own content URI and read access for the chosen app. No file is copied and no permission is needed. It sends the file **as opened**: with unsaved edits the app says so first and offers to save a copy (share that copy to send the edits). |
| Recent files | The empty screen and **⋮ → Recent files** list up to 12 documents with their last page; long press removes one. The app keeps the picker's grant (`takePersistableUriPermission`) and gives it back when a file leaves the list. The list (`files/recent.json`) holds locations, file names and positions only: no document text, no passwords. A file opened with `ACTION_VIEW` has no grant to keep and is not listed. |
| Restore | When Android ends the process in the background, the saved state reopens the document at the same page, place and zoom (a password is asked again; it is never stored). |
| Page indicator | "3 / 24" in the toolbar. Tap it to go to a page. |
| Search | The engine's text extraction and search rules (the same as the desktop and the web demo). Highlights on the page, match count, previous and next. |
| Encrypted PDFs | Password prompt; a wrong password is refused. |
| Night pages | **⋮ → Night pages**: the engine's night mode, each page inverted after drawing. |
| Dark mode | The toolbar follows the system's dark theme, also when it switches while the app is open. |
| Rotation | The document stays open; the page at the top of the view stays there. |
| CMYK images | CMYK and YCCK JPEG images are decoded (the Skia backend's managed decoder) and drawn as on Windows. |
| Existing annotations and fields | The engine draws each annotation's and widget's appearance into the page. Fillable fields are tinted light blue (read-only ones are not). |
| Form filling | Tap a field. A text field opens a dialog with the soft keyboard: one line or several, hidden for a password field, at most the field's `/MaxLen` characters. A check box or radio button switches at once. A combo box or list box offers its options (several for a multi-select list; an editable combo box also takes typed text). Read-only fields say so and do not change. After each value the engine writes it with a new appearance and recalculates calculated fields (formats, ranges and simple sums, the desktop's rules, **without** running the document's JavaScript); a value that a field's rule refuses goes back and the rule's message is shown. |
| Markup | Select text (long press, handles), then **Highlight**, **Underline** or **Strike** in the selection bar. |
| Ink and notes | **✎** opens the annotation bar. **Draw**: one finger draws on a page, two fingers still zoom. **Note**: tap where a sticky note goes, then type its comment. |
| Choose, edit, delete | Tap an annotation to choose it (outlined); a note (or any annotation with text) opens its comment to read or change. **Delete** removes the chosen one with its replies. |
| Undo and redo | **↶** and **↷** in the annotation bar, for every annotation change (the store keeps 100 steps). |
| Annotation list | **⋮ → Annotations**: every annotation with its page, kind and the start of its text, replies under the annotation they answer; tap one to go to it. |
| Save a copy | **⋮ → Save a copy** opens the system's save dialog (`ACTION_CREATE_DOCUMENT`) with "*name* (edited).pdf". The bytes are the file as opened plus one incremental update with the edits (a damaged file is written whole, as on the web). Opening another file or leaving with unsaved edits asks first: save a copy, discard, or cancel. If writing fails, the unfinished file is removed. If Android ends the app while the save dialog is open, the edits are gone: the file the dialog created is removed and the reader is told, never written with other bytes. |
| Signed and certified files | The web viewer's rules (Acrobat's): a certification that allows no changes allows no edits, one for form filling allows only that, and a signed file is changed only by an incremental update, so its signatures stay valid. Before the first change the reader is told that changes are added as a new revision. A document whose permissions do not allow annotations or form filling is not changed. |

## Architecture

```
 Android app (PdfViewer.Android, net10.0-android)
 ┌───────────────────────────────────────────────────────────────────────────┐
 │ MainActivity     toolbar, menu, search and selection bars, document       │
 │                  picker, outline, share, recent files, saved state;       │
 │                  .Edits: fields, markup, ink, notes, list, save a copy    │
 │ PageView         gestures, selection handles, render loop, tiles,         │
 │                  field tint, ink strokes, taps on fields and annotations  │
 │ ThumbnailView    page grid                                                │
 │ SkiaView         a View that Skia draws into (Android bitmap pixels)      │
 ├───────────────────────────────────────────────────────────────────────────┤
 │ PdfViewer.Web.Engine (net9.0, no UI; shared with the browser demo)        │
 │   ViewerDocument        open, render, search, outline                     │
 │   ContinuousPageLayout  page column, zoom anchoring, saved view           │
 │   PageTiles, TileCache  deep-zoom tiles within a byte budget              │
 │   PageImageCache<T>     rendered pages within a byte budget               │
 │   ViewerTextLayout      selection geometry and copied text                │
 │   ThumbnailGridLayout, ViewerOutline, RecentDocuments                     │
 │   ViewerEditSession     edits, their rules, writing, undo (shared store)  │
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
3. **Drawing.** The renderer writes BGRA pixels. Each page or tile is copied once into an
   `SKImage` (native memory, not the Java heap) and kept in an LRU cache by bytes. A tile is the
   renderer's pixel region of the page (`ViewerDocument.RenderRegionAsync`); the renderer keeps
   each page's analysis, so its images are decoded once, not once per tile. `SkiaView` draws the
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
4. **CFF fonts are catalogued too** (`.otf` files, and CFF faces in `.ttc` collections such as
   Noto Sans CJK): the Skia backend, the only renderer on Android, loads them itself. On the
   Android 13 emulator the catalog has 218 faces, 41 of them from `.otf` files and 14 Noto CJK
   faces. The Windows catalog still reads TrueType outlines only.
5. **Roboto's real bold.** Android 12 and later ship Roboto as one variable font. SkiaSharp
   3.116 has no API to set a variation axis, but Android's font manager reads
   `/system/etc/fonts.xml`, which names an axis value for each weight. `SkiaFontSource.LoadInstance`
   takes the bold instance from it (the style whose font data, the `head` table, is the same
   file) instead of emboldening the regular. On the emulator, "Weight" at 100 px has 7607 dark
   pixels regular, 10306 emboldened and 11140 with the bold instance.

Each substituted glyph is still placed and fitted by the document's `/Widths`, as on Windows.
Windows is unchanged: its catalog has no aliases, and every new path is behind
`PdfPlatform.IsAndroid`. Tests simulate Android on Windows (`PlatformGuardTests`).
### Cryptography

.NET for Android implements `System.Security.Cryptography` with the platform's own providers, so
MD5, SHA-1, SHA-256, AES and RSA are all available. The engine's `PortableCrypto` uses the
platform implementation wherever it works, and keeps its managed MD5 and AES only for runtimes
without them (browser WebAssembly).

## Build and install

Prerequisites: the .NET 10 SDK, its `android` workload, the Android SDK with platform 36, and
JDK 17 or 21. The app targets API level 36 (`net10.0-android`), which Google Play requires
(docs/android-release.md).

```powershell
dotnet workload install android
dotnet build src/PdfViewer.Android -f net10.0-android -c Release -p:AndroidPackageFormat=apk `
  -p:AndroidSdkDirectory="<Android SDK>" -p:JavaSdkDirectory="<JDK>"
adb install -r src/PdfViewer.Android/bin/Release/net10.0-android/io.github.ramanacr.pdfviewer-Signed.apk
```

- **Package format.** A Release build makes an Android App Bundle (`.aab`) for Google Play;
  `-p:AndroidPackageFormat=apk` makes an APK to install with `adb`. Debug builds make an APK.
- **SDK 10.** If several SDKs are installed, pin SDK 10 (a `global.json` that you do not commit),
  so that the net10.0-android workload is used. CI does this too. Without the SDK 10 workload,
  `-p:AndroidNet9=true` builds `net9.0-android` with the .NET 9 workload (API 35): fine for the
  emulator and `adb`, refused by Play.
- **Signing.** The build signs the package with the .NET SDK's per-user debug key, which is outside
  the repository. That is what `adb install` needs. The repository holds no keystore. The store
  release (`.github/workflows/android-release.yml`) signs with the upload key from GitHub secrets;
  see docs/android-release.md.
- **Windows path length.** The Release build compiles the startup code ahead of time (Mono AOT).
  From a very deep checkout path, that step fails with `Precompiling failed … condition 'corlib'
  not met`. Build from a short path (for example `subst P: <checkout>`).
- **CI.** `.github/workflows/android.yml` builds the APK on Ubuntu, checks that the manifest
  asks for no permission, and uploads the APK as the `pdf-viewer-android-apk` artifact. It is a
  separate workflow that runs only when the app or the code it is built from changes, so the
  main CI job is not slowed down. It also checks that the target API level is 36 and that the
  store listing in `fastlane/metadata/android/en-US/` is within the stores' length limits.
- **Store images.** `scripts/android_store_images.ps1` makes the launcher icons
  (`Resources/mipmap-*`), the 512 × 512 store icon and the 1024 × 500 feature graphic from
  `assets/app_icon.png`.

### Size

The Release APK holds two ABIs, `arm64-v8a` (phones) and `x86_64` (emulators): **24.7 MB**
(23.5 MiB; 24.1 MB for the proof of concept). The largest parts, per ABI, as measured for the
proof of concept: `libSkiaSharp.so` about 9 MB, the .NET assemblies about 5.2 MB, the Mono runtime about 3.2 MB, ahead-of-time code for
`System.Private.CoreLib` 1.7 MB. An arm64-only APK would be about half that size.

## What is verified

On the development machine (Windows 11), with the Android 13 emulator image (Pixel 3a, API 33,
x86_64, 1080 × 2220), the Release APK was installed with `adb` and driven with `adb shell input`.
The test PDFs were written in code (6 pages with an outline, a CMYK JPEG, bold and 2 pt text;
and one encrypted without the copy permission). Screenshots were taken, not committed. The store
screenshots (`fastlane/metadata/android/en-US/images/phoneScreenshots/`, 28 October 2026) come
from a later run on the same emulator image.

| Checked | Result |
|---|---|
| Install and start | Starts, shows the empty state. |
| Document picker | **Open** shows the system picker; the chosen file opens. |
| Rendering and fonts | A 3-page test PDF with non-embedded Helvetica, Helvetica-Bold, Times-Roman and Courier draws all text: Roboto, Noto Serif and Droid Sans Mono, fitted to the widths. Shapes and colours are correct. |
| Roboto bold | The bold instance is found ("sans-serif", weight 700) and used for Helvetica-Bold; see Fonts for its ink. |
| CMYK JPEG | The image draws in its colours (yellow to red in its left half, cyan to blue in its right half), not black and not blank. |
| Deep zoom | Double tap to 2.5×, then + three times to 8×: a 10 pt label is drawn with pixel-sharp edges (one pixel of antialiasing), no seams seen between tiles. |
| Outline | **⋮ → Outline** lists "Chapter 1: Start (1)", "Chapter 2: Middle (3)", indented "Section 2.1 (4)", "Appendix (6)"; tapping Section 2.1 shows page 4. |
| Pages | **⋮ → Pages** shows the 6 thumbnails in 3 columns with page 4 marked; tapping page 2 shows page 2. |
| Selection and copy | A long press selects "fox"; dragging the end handle extends it to "dog." and then to the end of the next line; **Copy**, then a paste into the search field, gives the selected text (checked for one word and for two lines). |
| Copy permission | In the PDF without the copy permission a long press selects nothing and shows "This document does not allow copying its text." |
| Share | **⋮ → Share** shows the system share sheet with the file's name (Bluetooth, Drive, Gmail, Messages offered). Nothing was sent. |
| Recent files | After a restart the empty state lists the file with its last page; tapping it opens it there. The page follows scrolling (recorded when the app goes to the background). **⋮ → Recent files** lists both test files. |
| Restore after process death | At 8× on page 1: Home, `adb shell am kill`, the process is gone; starting the app again gives a new process that shows the same document at 8× at the same place. |
| Encrypted PDF | The AES-128 fixture (`tests/PdfViewer.Tests/Fixtures/encrypted.pdf`) asks for its password, refuses a wrong one, opens with the right one and renders (proof of concept run). |
| Search | "android" finds 6 matches on 3 pages; highlights sit on the words; next match works (proof of concept run). |
| Night pages, dark mode, rotation | Pages are inverted; dark mode and landscape keep the document open (proof of concept run). |
| Permissions | The merged manifest has no `uses-permission` element (also checked by CI); `dumpsys package` lists no runtime permission. |
| Logs | No crash, no unhandled exception and no SELinux denial for the app in `logcat` during the run. |
| Form filling (phase 2) | With a test form written in code: a tap on the name field opens the dialog with the soft keyboard; "Ada Lovelace" is drawn into the field. The comb field with `/MaxLen 5` takes only "12345" of nine typed digits. The check box and a radio button switch on a tap; the combo box changes from Paris to Rome; the list box selects Banana. Fields are tinted. |
| Save a copy (phase 2) | **⋮ → Save a copy** offers "form (edited).pdf" in the system's save dialog. The saved file starts with the original bytes unchanged and adds one incremental update. After saving, opening another file does not ask about unsaved changes. With the app ended (`am kill`) while the save dialog was open, **Save** leaves no file behind and the reader is told the changes were lost. With unsaved edits, **Share** first says that it sends the file as opened. |
| Annotations (phase 2) | A long press and **Highlight** or **Strike** mark a word; **Draw** draws a stroke with one finger; **Note** and a tap place a note after its text is typed. A tap on the drawing outlines it; **Delete** removes it and **↶** brings it back. **⋮ → Annotations** lists "Highlight", "Strike-through", "Drawing" and "Note · Check this". With unsaved changes, **Open** asks to save a copy, discard or cancel. |

The runs found these bugs, all fixed: the soft keyboard service was asked for under the wrong
name (`InputService` instead of `InputMethodService`), which crashed the search bar; SELinux
logged a denied file lock for every font read, now avoided with `System.IO.DisableFileLocking`;
the page indicator and the recent list could take the last page while the page view had no size
yet; the selection handles of a short word overlapped; the selection bar pushed the pages down
(it is now drawn over them).

## What is not verified

1. **A physical phone.** Only the x86_64 emulator was used. The arm64 code is built but has not
   run.
2. **Pinch zoom**, and with it the stretched tiles while a pinch runs and the two-finger zoom
   while the draw tool is on. `adb` cannot send a
   two-finger gesture. The pinch code uses Android's `ScaleGestureDetector`; the zoom-anchoring
   math and the tile geometry are unit-tested.
3. **"Open with" (`ACTION_VIEW`)** from another app, sharing and restoring such a file (its grant
   comes with the intent), and the go-to-page dialog.
4. **Android 14 and 15.** On Android 15 apps are drawn edge to edge; the layout uses
   `fitsSystemWindows`, which should keep the toolbar below the status bar, but it was not seen.
5. **Restore through the system's recent-apps screen** (only a new start from the launcher
   intent was tried), and restore of a password-protected file (the password is asked again).
6. **Performance and memory** on real documents: large pages, many images, CJK text, a large
   CMYK JPEG (the managed decoder is slower than a native one), memory under pressure.
7. **Certificate-encrypted PDFs.** RSA is available on Android, but the app has no certificate
   store and was not tested with such a file.

## Known gaps

1. **Fonts.** Emoji fonts are bitmaps and are not used. A variable font other than those that
   `fonts.xml` lists by weight still gets simulated bold.
2. **Rendering** has the Skia backend's gaps (see its README): arithmetic-coded and 12-bit JPEG
   images are not decoded, and small text has slightly less ink than Direct2D draws.
3. **Selection** is on one page at a time, and the view does not scroll by itself while a handle
   is dragged to the edge. No floating copy toolbar: the bar over the pages holds the actions.
4. **Not in the app:** printing, a document properties view, and these annotation tools of the web
   viewer: free text, shapes, lines, stamps, replies, moving an annotation, colours, XFDF.
5. **Recent files** lists only files with a persistable grant (the document picker gives one);
   a file another app handed over with `ACTION_VIEW` is opened but not listed.
6. **Edits are not kept when Android ends the process** in the background (only the view is); a
   copy must be saved first. Saving over the opened file itself is not offered, only a copy.
7. **Markup needs a text selection**, so a document that does not allow copying cannot be
   highlighted. A note's icon is 20 points, a small target at fit width. With the draw tool on,
   one finger only draws: scrolling needs the tool off.

## Tests

- `tests/PdfEngine.Vector.Tests/PlatformGuardTests.cs`: Android simulated on Windows (font folder,
  alias catalog, threads), family aliases in `Match`, `FindFamily` and `Fallbacks`, the alias
  table covering every family that substitution asks for, and CFF faces in the Android catalog
  only.
- `tests/PdfEngine.Vector.Skia.Tests/AndroidViewerTests.cs`: the page layout (fit width, visible
  and current pages, zoom anchoring, rotation, search rectangles), the image cache, non-embedded
  text drawn with an aliased stand-in through `ViewerDocument`, and a variable font's bold
  instance from the font manager (Windows' Bahnschrift).
- `tests/PdfEngine.Vector.Skia.Tests/AndroidAppTests.cs`: tiles (which ones cover the view, cut
  edges, stretched during a pinch, stitched tiles equal to the whole page), the tile cache, word
  selection and handles, the copy permission, the outline list, the thumbnail grid, the recent
  list (order, capacity, a restart, damaged files) and a saved view restored in a new size.
- `tests/PdfEngine.Vector.Skia.Tests/AndroidEditTests.cs`: touches mapped to page points (gaps and
  margins on no page, zoomed and scrolled), the form value rules (/MaxLen, line breaks, read-only,
  options, states), fields written, drawn and saved as an incremental update, a value refused by the
  field's rule, certified documents (no changes; form filling only, with the notice once), notes, ink
  and highlights made from touches and read back from the saved file, editing and deleting a file's
  own annotation, undo and redo, the annotation list, and one save at a time.
- `tests/PdfEngine.Vector.Skia.Tests/CmykJpegTests.cs`: the CMYK and YCCK JPEG decoder.