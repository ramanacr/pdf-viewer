# Roadmap

What is planned next for PDF Viewer Native. Plans change; dates are not promised. Items marked
**help wanted** have an issue you can pick up. Read [CONTRIBUTING.md](CONTRIBUTING.md) first.

## In progress

- **Comment reply threads on the desktop.** Replies nested under each comment, edit and delete
  with undo, and review status (Accepted, Rejected, Completed) that Acrobat and the web viewer
  also read.
- **Android: comment replies, review status, XFDF and kept edits.** Reply threads with review
  status as the desktop writes them, XFDF import and export through the system's file dialogs,
  and unsaved edits kept when Android ends the app in the background.

## Next

### Desktop (Windows)
- Import and export comments as XFDF. **help wanted:** [#140](https://github.com/ramanacr/pdf-viewer/issues/140).

### Web viewer and embed SDK
- Drag one point of a line, arrow, polygon or polyline. **help wanted:**
  [#139](https://github.com/ramanacr/pdf-viewer/issues/139).
- Callout text boxes (free text with a leader line).
- Custom stamps from a picture or a PDF page, as on the desktop.
- One editing core for the web viewer and Android (the shared `ViewerEditSession`).
- Self-hosted Noto fonts in the live demo, loaded per script on demand, so text in any script
  shows without a font on the visitor's machine and without calls to third-party font servers.

### Android
- Choose the colour of new highlights, ink and notes. **help wanted:**
  [#141](https://github.com/ramanacr/pdf-viewer/issues/141).
- A larger tap target for sticky-note icons. **help wanted:**
  [#142](https://github.com/ramanacr/pdf-viewer/issues/142).
- Production release on Google Play after the closed test.

### Engine (NuGet packages)
- PdfEngine 1.0.0: the stable release after the 1.0.0-rc.1 feedback. From 1.0.0 on, package
  validation blocks breaking changes until 2.0.

## Something missing?

Open an [issue](https://github.com/ramanacr/pdf-viewer/issues) to suggest a feature or to offer
to build one.

## Done recently

See the [releases](https://github.com/ramanacr/pdf-viewer/releases) and
[docs/features.md](docs/features.md).
