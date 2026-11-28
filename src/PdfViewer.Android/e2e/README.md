# End-to-end scenarios (Maestro)

Scripted runs of the app on a device or emulator, for checking it after a change without
watching the screen. Each flow in `flows/` drives one feature with [Maestro](https://maestro.dev)
(open-source, YAML); `run.sh` gives each flow a clean start on its document, runs it, checks the
files tools save on the host (`# expect-file`, `# expect-grep` in the flow's header) and the app's
crash log, and prints one line per flow:

```
PASS 05-organize
FAIL 11-output-preview: Assertion is false: ".*Cyan.*" is visible  [screen: e2e/out/11-output-preview.png, log: …]
13 passed, 1 failed
```

A screenshot is taken only when a flow fails.

## Run

Needs `adb` with one device or emulator, the app installed (or `--install <apk>`), Maestro's CLI
(`curl -Ls https://get.maestro.mobile.dev | bash`, Java 17 or later) and Python 3 with Pillow (it
makes the image-only scan the OCR flow reads).

```bash
src/PdfViewer.Android/e2e/run.sh --install src/PdfViewer.Android/bin/Debug/net10.0-android/io.github.ramanacr.pdfviewer-Signed.apk
```

A filter runs some flows only: `run.sh ocr`. A Debug build for this must embed its assemblies
(`-p:EmbedAssembliesIntoApk=true`), since Fast Deployment needs Visual Studio or `dotnet run`.

The test documents are `samples/SampleDocument.pdf` and the generated scan; they are pushed to
`/sdcard/Documents/pdfviewer-e2e` (out of reach of the `expect-file` clean-up in `/sdcard/Download`) and opened through the media store, as another app's "Open with" would. Waits are
long (minutes for Output preview and OCR) so the flows also pass on an emulator without hardware
acceleration.

## Flows

| Flow | Checks |
|---|---|
| 01-open-properties | The document opens; Document properties shows its version and 7 pages. |
| 02-view-menu | Outline, read mode (A+), single-page layout and a swipe to page 2, back to continuous, rotate view right and left. |
| 03-search | A search finds "Keyboard"; the results list shows it. |
| 04-annotate | A note and a rectangle; undo and redo; the note is in the annotation list. |
| 05-organize | Page 3 turned right and saved: the file has `/Rotate 90`. |
| 06-extract | Extract pages saves a PDF. |
| 07-protect | Protect with password saves a copy with `/Encrypt`. |
| 08-reduce | Reduce file size reports a smaller copy, or that the file is compact already. |
| 09-export-text | Export text saves a .txt with the document's text. |
| 10-checks | Accessibility check, Document safety and Preflight (with Save as JSON) show their reports. |
| 11-output-preview | The page's plates are listed (Cyan, Magenta). |
| 12-ocr | Make scanned pages searchable recognizes words; the copy has a ToUnicode text layer. |
| 13-redact | An area redaction saves a redacted copy. |
| 14-close-document | Close document goes back to the start screen. |

Not covered (they need a person or hardware): the camera, signing with a certificate, printing,
Read aloud's voice, windows in split screen.
