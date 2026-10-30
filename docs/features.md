---
title: Features
permalink: /features/
---

# Features of the Windows app

This page lists what the Windows app (PDF Viewer Native) does, with its menus and keyboard
shortcuts. For downloads and an overview, see the [README](https://github.com/ramanacr/pdf-viewer#readme).
For the browser demo and the Android app (a preview), see their own READMEs
([web](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Web/README.md),
[Android](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Android/README.md)).

- [Viewing and navigation](#viewing-and-navigation)
- [Reading and accessibility](#reading-and-accessibility)
- [Text selection and search](#text-selection-and-search)
- [Comments, measuring and stamps](#comments-measuring-and-stamps)
- [Editing and page organisation](#editing-and-page-organisation)
- [Forms, signatures and security](#forms-signatures-and-security)
- [Create, convert and export](#create-convert-and-export)
- [Printing and print production](#printing-and-print-production)
- [Text in fonts that a PDF does not embed](#text-in-fonts-that-a-pdf-does-not-embed)
- [Windows integration](#windows-integration)
- [Optional components](#optional-components)
- [User guide](#user-guide)
- [Keyboard shortcuts](#keyboard-shortcuts)

## Viewing and navigation

- **View modes.** Continuous vertical scrolling, single page, and two-page (facing) view. The
  facing view can keep the cover on its own, so the spreads line up with the printed book. The
  toolbar button cycles continuous → single page → facing.
- **Zoom from 25 % to 1600 %.** Fit to width (`Ctrl+1`), fit to page (`Ctrl+0`), the slider,
  presets, and `Ctrl` + mouse wheel. Pages stay sharp at every zoom: the engine draws them again
  as vectors, and never scales up a bitmap.
- **Pan.** Use the hand tool, or drag with the middle mouse button.
- **Rotate the view** 90° clockwise (`Ctrl+R`) or counter-clockwise (`Ctrl+Shift+R`).
- **Night mode** (`Ctrl+Shift+N`) inverts the pages. It is separate from the light and dark
  theme of the window.
- **Full screen** (`F11`; `F11` or `Esc` to exit).
- **Navigation history** (`Alt+Left`, `Alt+Right`). Bookmarks, search hits and the page box are
  recorded. Ordinary scrolling is not, so "back" never undoes your reading.
- **Bookmarks** (the document outline) as a tree, and **page thumbnails** in the sidebar
  (`Ctrl+B` shows or hides it).
- **Document tabs.** Open several documents (`Ctrl+Tab` and `Ctrl+Shift+Tab` switch, `Ctrl+W`
  closes).
- **Large documents.** A priority scheduler and byte-limited caches keep memory bounded. Only the
  visible and nearby pages are kept in memory (60 pages at most by default).

## Reading and accessibility

- **Read mode** (`Ctrl+4`) reflows the content (size with `Ctrl+Plus` and `Ctrl+Minus`). It
  follows the dark theme and night mode. UI Automation exposes it as a text document, so
  Narrator, NVDA and JAWS read it by paragraph, line, word and character. The document's
  accessibility-extraction permission is honoured.
- **Tagged PDFs** (ISO 32000-2 14.8) are read in structure order. Headings, paragraphs, list
  items, table cells, quotes and code keep their roles. Figures are described by their `/Alt`
  text, `/ActualText` replaces what it stands for, and artifacts (running headers, page numbers)
  are left out.
- **Untagged pages** get paragraphs from the text layout, headings from larger text, list items
  from their markers, and vertical text read as words.
- **Read Aloud** (*Tools → Read Aloud*) reads from the current page with the voice built into
  Windows. It is an [optional component](#optional-components).
- **Accessibility Check** (*Tools → Accessibility → Accessibility Check*) checks PDF/UA-1. It
  lists each issue with its Matterhorn checkpoint and shows where it is on the page.
- **Make Accessible** (*Tools → Accessibility → Make Accessible*) tags an untagged PDF in a new
  copy. It finds headings, paragraphs, lists, simple grid tables, figures (with placeholder
  alternate text to review), links, and headers and footers as artifacts, in reading order
  (columns and turned pages included). The pages read and draw as before. It then reports the
  PDF/UA checks that still need a person.

## Text selection and search

- **Selection like a word processor**, from the engine's text page (reading order, inferred
  spaces and line breaks, line-end hyphens):
  - Click and drag selects character by character, across lines, columns and pages. The view
    scrolls when the pointer is near the edge.
  - Double-click selects the word (`don't`, `3.14` and `snake_case` stay whole). Double-click and
    drag extends by words. Triple-click selects the paragraph, `Ctrl`+click the sentence.
  - `Shift`+click extends the selection. `Shift+←/→` moves by character, `Ctrl+Shift+←/→` by word,
    `Shift+↑/↓` by line, `Shift+Home/End` to the ends of the line, `Ctrl+Shift+Home/End` to the
    ends of the page.
  - Copy (`Ctrl+C`) keeps line breaks and joins words hyphenated across lines. `Ctrl+A` selects
    the current page.
- **Search** (`Ctrl+F`) over the engine's text page, with match case, a match counter
  ("Match 2 of 14 (Page 3)"), `F3` and `Shift+F3`, and a **Search Results** tab with snippets.
- **Searchable scans (OCR).** *Tools → Make Scanned Pages Searchable* adds an invisible text layer
  to pages that have none, with the OCR built into Windows. Nothing is uploaded.

## Comments, measuring and stamps

- **Annotations:** highlight, underline, strikethrough, note, free text, rectangle, ellipse,
  line, arrow, polygon, connected lines and freehand ink. A highlight from a selection covers each
  selected line (one quad per line).
- **Lines and polygons** (toolbar, or *Comment → Draw*):
  - Line and arrow: drag from where the line starts to where it ends. An arrow has an open arrow
    head at its end. `Esc` during the drag drops the line.
  - Polygon and connected lines: click each point. Double-click or press `Enter` to finish.
    `Backspace` removes the last point. `Esc` drops the shape (press it again to stop the tool).
    A polygon needs three points. Connected lines need two.
  - They use the colour and line width chosen in the toolbar. You can select, move, nudge and
    delete them, and undo each change. You cannot resize them or move single points.
  - They are saved as the annotations other readers expect: Line (with `/L`, and `/LE` for the
    arrow head), Polygon and PolyLine (with `/Vertices`), each with an appearance stream. They have
    no `/Measure`, so they are not measurements. When you open the file again, they come back as
    the same tools. *Save as XFDF* exports them too.
- **Shape style.** Select a rectangle, ellipse, line, arrow, polygon or connected lines and press
  `Enter` (or double-click it) to open its comment. The *Shape style* part of that window sets:
  - **Fill**: none or a colour (`/IC`). It fills a rectangle, ellipse or polygon, and the closed
    line endings (closed arrow, circle, square, diamond) of a line.
  - **Line style**: solid, dashed or dotted (`/BS` with `/S /D` and a `/D` dash).
  - **Start** and **End** (lines and connected lines only): None, Open arrow, Closed arrow,
    Circle, Square, Diamond, Bar (`/Butt`), Reversed open arrow, Reversed closed arrow or Slash
    (`/LE`). A line with an ending is an arrow; an arrow with no endings is a line.
  - Each change is one undo step. The save draws the new appearance stream, and the screen shows
    the shape as it is saved: dashed lines with solid endings.
  - A shape from another program keeps its own fill, dash and endings until you change that one
    value. Measurements have no shape style.
- **Save** (`Ctrl+S`) writes the annotations into the document, editable. The file is written to
  a temporary file first and then replaces the original, so a failure leaves your file as it was.
  Other ways to save: flattened into the page, or exported as XFDF.
- **Edit placed annotations.** Click an annotation to select it; double-click or `Enter` opens its
  comment. Drag it to move it, and drag a handle to resize it (`Shift` keeps the proportions).
  Lines, arrows, polygons and connected lines move as a whole; they have no resize handles.
  Turn a stamp with its rotation handle (`Shift` snaps to 15°). Drag the points of a measurement;
  the result is measured again. Arrow keys nudge by 1 pt (`Shift`: 10 pt), `Delete` removes, `Esc`
  lets go, `Tab` goes to the next annotation. Every change is one undo step.
- **Measuring** (*Tools → Measure*): distance, perimeter and area.
  - The info panel shows each segment, the total, the area, the angle and the pointer position in
    the chosen units.
  - Points snap to the ends, middles and lines of the page's drawing (hold `Shift` to place a
    point freely).
  - The scale comes from the page when it has measuring viewports (`/VP` with `/Measure`,
    ISO 32000-2 12.9). Otherwise you set it (for example 1 in = 10 ft).
  - A measurement is saved as the annotation that Acrobat writes (Line, PolyLine or Polygon with a
    `/Measure` dictionary).
- **Stamps** (*Comment → Stamp*):
  - The standard business stamps (Approved, Confidential, Draft, Final, Received, Reviewed, Void
    and more) and dynamic stamps that add your name, the date and the time. Your name is kept in
    the viewer's settings and never sent anywhere.
  - Custom stamps from a PNG or JPEG picture or from a PDF page, kept in a local library under
    `%LOCALAPPDATA%\PdfViewerNative`.
  - A stamp is a Stamp annotation with its own appearance. It survives saving, flattening,
    PDF/A conversion and printing.

## Editing and page organisation

- **Edit text and images** (`Ctrl+E`). Edit page text in place, in its own font, size, colour and
  alignment, with paragraphs reflowed. Move, resize, rotate, replace or delete images. Add new
  text and images. Right-to-left (Arabic, Hebrew) and Indic scripts are shaped, and vertical
  Japanese and Chinese columns are kept. Every edit can be undone, and changes are saved as an
  incremental update.
- **Organize Pages** (*File → Organize Pages*): reorder, rotate, duplicate or leave out pages, and
  save the result as a new document.
- **Page operations:** rotate, insert blank pages, delete, extract page ranges, merge documents,
  and split by page count, with undo and redo.
- **Page Marks** (*Edit → Page Marks*): headers and footers, watermarks, backgrounds and Bates
  numbering, added or removed as a set.
- **Reduce File Size:** recompresses images, removes unused objects and subsets fonts. Encrypted
  documents keep their passwords and permissions.
- **Compare With Document** (*Tools*): text differences (added, deleted, changed) and a visual
  difference heatmap, side by side with synchronised scrolling.

## Forms, signatures and security

- **Forms (AcroForm).** Fill text fields, check boxes, radio buttons, lists and combo boxes, with
  tab order, formatting, validation and calculations. The Acrobat `AF*` functions are recognised;
  no script is run. *Tools → Import / Export Form Data* (XFDF and FDF), *Reset Form* and *Flatten
  Form*.
- **Digital signatures.** Sign with a certificate (a `.pfx` file or the Windows certificate store),
  visible or invisible, with an optional picture and an RFC 3161 timestamp, and with long-term
  validation (PAdES-B-LT and B-LTA). *Tools → Verify Digital Signatures* checks signatures and what
  changed after signing.
- **Password protection.** *Tools → Protect with Password* (AES-256, with print, copy, edit and
  form permissions) and *Remove Security*. Encrypted files open with either password, and their
  permissions are honoured.
- **Redaction.** Mark text, an area or every search result, then apply. The text, image pixels
  and drawings under the marks are removed from the file, not only covered, and the copy keeps no
  earlier revision.
- **Document safety.** Every document is inspected when it opens. *Tools → Document Safety* shows
  scripts, launch actions and embedded files; *Tools → Save Clean Copy* writes a copy without
  them. *File → Embedded Files* lists attachments and extracts one only when you ask. See
  [privacy](privacy.md).

## Create, convert and export

- **Create PDF** (*File → Create PDF*), all on this computer:
  - **From images:** JPEG, PNG, TIFF (every page), BMP, GIF, and WebP or HEIC when Windows has
    their decoders. JPEGs are kept byte for byte. Greyscale, text recognition (Windows OCR) and
    PDF/A-2b are options.
  - **From a scanner** (Windows Image Acquisition), from the glass or the feeder.
  - **From a file:** Word, Excel and PowerPoint files, RTF and HTML through Microsoft Office's own
    PDF export. Text files are laid out by the app itself when Office is not installed.
  - **Combine files:** PDFs, pictures and Office files in one PDF, with a bookmark for each file.
- **PDF/A** (*Tools → PDF/A: Check and Convert*): validates and converts to PDF/A-1b, 2b and 3b.
- **Export to Office** (*File → Export*), written by the app itself, with no Office components:
  - **Word (.docx):** text that flows with its fonts, headings, lists, tables, images, links,
    headers and footers.
  - **Excel (.xlsx):** each table on its own sheet, with merged cells, column widths, fonts,
    borders and shading. Numbers, currency, percentages and dates become values that Excel can
    calculate with.
  - **PowerPoint (.pptx):** a slide for each page, with editable text boxes, tables, pictures and
    shapes.
- **Export Text** (*File → Export Text*) writes the text layer to a plain text file.
- **Export to Images** (*File → Export to Images*): PNG (the default), JPEG (with a quality
  setting), TIFF or BMP at 150, 300 or 600 DPI, for all pages, the current page or a range. Files
  are named `prefix_page_0001.png`, `prefix_page_0002.png` and so on.

## Printing and print production

- **Print** (`Ctrl+P`) opens the app's own print dialog with a live preview: printer, orientation,
  copies, collation, colour or greyscale, and page ranges. Pages print as vectors through
  Direct2D, or as 300 DPI page images from the same renderer as the screen.
- **Output Preview** (*Tools → Print Production → Output Preview*) shows the process and spot
  plates of a page. You can turn plates on and off, simulate overprint, highlight total ink over
  a limit (300 % by default) and read the ink of each plate under the pointer. RGB is separated
  through the engine's own SWOP model, so its plate values are approximate.
- **Preflight** (*Tools → Print Production → Preflight*) checks a document against *Digital
  printing (basic)* or *PDF/X-1a readiness*: fonts, image resolution, RGB colour, total ink,
  hairlines, transparency, spot colours, page boxes and the output intent. Reports export as text
  or JSON, without page text. Preflight only checks; it does not convert to PDF/X.
- **Colour management.** ICC profiles in the document are honoured. CMYK colour is shown as
  Acrobat shows it.

## Text in fonts that a PDF does not embed

When a PDF names a font but does not embed it, the app draws the text with an installed font:

- Arabic and Indic text is shaped: Arabic letters join, lam-alef forms a ligature, marks sit on
  their letters, Indic vowel signs are reordered, and conjuncts and reph are formed. The
  Direct2D and WPF renderers and the web demo use the same shaping code.
- When no single installed font has every character of a line, each missing character comes from
  another installed font that has it (for example Nirmala UI for Hindi). Each Arabic or Indic word
  comes from one font, so its letters still join.
- Chinese, Japanese and Korean text uses the font of its language: Yu Gothic for Japanese,
  Microsoft JhengHei for Traditional Chinese, Malgun Gothic for Korean, and Microsoft YaHei for
  Simplified Chinese. The language comes from the PDF's character collection, kana or hangul in
  the text, or the font name.
- Hyphenated font names such as `MS-Gothic,Bold` are recognised. ZapfDingbats is drawn with
  Segoe UI Symbol.
- The app reads the fonts in `C:\Windows\Fonts` and the fonts installed for the current user only
  (`%LOCALAPPDATA%\Microsoft\Windows\Fonts`). OpenType fonts with CFF outlines (`.otf`, for
  example Source Han Sans) are read too.
- Text that you type (text boxes, form fields, page text editing, signature appearances) can use
  these fonts. A TrueType font is used first when it has the characters. An installed CFF font
  is used only for characters that no TrueType fallback font has, and the app embeds only the
  glyphs it uses. Installed CFF icon and symbol fonts are not used as fallback fonts.

## Windows integration

- **File icon and preview.** PDFs show the PDF file icon, also when the viewer was chosen through
  "Open with" or Default apps. The app registers Explorer preview metadata.
- **Command line:** `PdfViewer.exe --thumbnail <input.pdf> <output.png> [dpi]` draws a page without
  a window. `PdfViewer.exe --register` and `--unregister` add or remove the file association.
- **Updates.** The Microsoft Store installs updates for the Store app, so it has no update check.
  The GitHub build (installer or portable app) can check GitHub for a newer release and run the
  new installer. The check is off until you allow it (*Help → Privacy & Safety*). From version 4.1.1, the download uses the
  first GitHub server address that answers. If you have 4.0.1 or 4.1.0, download
  `PdfViewerSetup.exe` once from the releases page.
- **Software bill of materials.** Every release has CycloneDX 1.6 (`sbom.cyclonedx.json`) and
  SPDX 2.3 (`sbom.spdx.json`) files.

## Optional components

Some features are not in the installer, so you carry them only if you want them. The installer
offers each one as a tick box, and the app can download one later when you ask.

| Component | Size | Contains |
| --- | --- | --- |
| Read Aloud | 669 KB | `System.Speech.dll`: text to speech with the voice built into Windows |

Rules for a download:

- **Never without a request.** The installer's box is off by default. Before anything happens,
  the app tells you what it will download, the size and the source.
- **Only from this project's GitHub release assets**, over HTTPS, at the URL for the running
  version, so the component always matches the app.
- **Checked against a SHA-256 hash in the app.** A file that does not match is deleted, not used.
- **The build refuses to package** a file that does not match the pinned hash.
- **A missing component never stops the app.** Only one factory method refers to the optional
  assembly.

The Microsoft Store package has Read Aloud inside it, so it downloads nothing
(see [Microsoft Store build](microsoft-store.md)).

## User guide

### Open a document

1. Click **Open** on the toolbar, or press `Ctrl+O`.
2. Or drag a `.pdf` file from Explorer onto the window.
3. Or select **File → Open Recent**, or a file in the *Recently Opened* list on the start screen.
4. To try the app, click **Open Sample Demo** on the start screen, or select
   **File → Open Sample Demo Document**.

### Navigate

- Use the **First**, **Previous**, **Next** and **Last** toolbar buttons, or `Home`, `PageUp`,
  `PageDown` and `End`.
- Type a page number in the page box and press `Enter`.
- Click a bookmark or a thumbnail in the sidebar.

### Search

1. Press `Ctrl+F` to open the search bar.
2. Type the text and press `Enter`.
3. Select **Match Case** for an exact match.
4. Press `F3` for the next match and `Shift+F3` for the previous match.
5. Open the **Search Results** tab in the sidebar to see all matches. Double-click a result to go
   to it.

### Export pages as images

1. Select **File → Export to Images**.
2. Select the output folder, the file prefix, the format (PNG, JPEG, TIFF or BMP), the resolution
   (150, 300 or 600 DPI) and the pages (all, current or a range). For JPEG, also select the
   quality: a higher quality makes larger files.
3. Click **Export**. The status bar shows the progress.

### Print

1. Press `Ctrl+P`.
2. Select the printer and the options. The preview shows the result.
3. Click **Print**.

### Document properties

Press `Ctrl+D`. The dialog shows the file name, path and size, the title, author, subject,
keywords, creator and producer, the dates, the PDF version, the page count and size, the
encryption and the engine status.

## Keyboard shortcuts

| Shortcut | When | Action |
| :--- | :--- | :--- |
| `Ctrl+O` | Always | Open a PDF |
| `Ctrl+S` | Document open | Save the document with its annotations |
| `Ctrl+Shift+S` | Document open | Save an annotated copy as |
| `Ctrl+P` | Document open | Print, with preview |
| `Ctrl+F` | Document open | Show or hide the search bar |
| `F3` / `Shift+F3` | Search active | Next / previous match |
| `Ctrl+C` | Text selected | Copy the selected text |
| `Ctrl+A` | Document open | Select all text on the current page |
| `Esc` | Text selected | Clear the selection |
| `Ctrl+Z` / `Ctrl+Y` | Document open | Undo / redo |
| `Ctrl+D` | Document open | Document properties |
| `Ctrl+E` | Document open | Edit text and images |
| `Ctrl++` / `Ctrl+-` | Document open | Zoom in / out |
| `Ctrl+0` | Document open | Fit to page |
| `Ctrl+1` | Document open | Fit to width |
| `Ctrl` + mouse wheel | Document open | Zoom |
| `Ctrl+R` / `Ctrl+Shift+R` | Document open | Rotate the view clockwise / counter-clockwise |
| `Ctrl+B` | Always | Show or hide the sidebar |
| `Ctrl+4` | Document open | Read mode |
| `Ctrl+Shift+N` | Document open | Night mode (invert pages) |
| `Ctrl+W` | Document open | Close the document |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Several documents | Next / previous tab (also `Ctrl+PageDown` / `Ctrl+PageUp`) |
| `Alt+Left` / `Alt+Right` | Document open | Back / forward through jumps |
| `F11` | Document open | Full screen (`Esc` or `F11` to exit) |
| `PageDown` / `PageUp` | Document open | Next / previous page |
| `Home` / `End` | Document open | First / last page |
| `Enter` or double-click | Measuring | Finish a perimeter or area |
| `Backspace` | Measuring | Remove the last point |
| `Enter` or double-click | Drawing a polygon or connected lines | Finish the shape |
| `Backspace` | Drawing a polygon or connected lines | Remove the last point |
| `Esc` | Drawing a line, polygon or connected lines | Drop the shape (with no polygon started: stop the tool) |
| `Shift` (held) | Measuring | Place a point without snapping |
| `Esc` | Measuring or placing a stamp | Drop the measurement (again: stop measuring) / cancel the stamp |
| Click / `Tab` / `Shift+Tab` | Page | Select an annotation / the next / the previous |
| Double-click or `Enter` | Annotation selected | Edit its comment |
| Arrow keys | Annotation selected | Nudge it 1 pt (`Shift`: 10 pt) |
| `Delete` | Annotation selected | Delete it |
| `Esc` | Annotation selected | Let go of it (while dragging: put it back) |
| `Shift` (held) | Dragging a handle | Keep the proportions / snap a turn to 15° / place a point without snapping |
| `Alt+F4` | Always | Exit |
