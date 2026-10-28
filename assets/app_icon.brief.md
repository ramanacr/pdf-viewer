# App icon brief (chosen 2026-10-28: concept B "Page + pen")

Replaces Flaticon 4649604 (app icon) and Flaticon 9159117 (PDF file icon), added in 4ee2289 with no recorded licence.

Subject:    a white document page with a folded corner, three text lines, and a pen writing on it (reading + editing).
Style:      flat vector, no gradients, no lettering.
Palette:    #c8102e (brand accent, site --accent), #ffffff, #f4c3cb (fold, pen cap), #1f2328 (pen body).
Background: rounded square #c8102e, radius 112 on a 512 grid (app icon). File-type icon: no tile.
Output:     SVG master (512 x 512 viewBox), PNG/ICO derived.
Variants:   3 drawn (A page, B page + pen, C page + lens); owner picked B.
Author:     drawn by hand in SVG by Claude Code (Recraft was unavailable in this session). Original work, MIT like the repo.

## Files

| File | Use |
| --- | --- |
| `app_icon.svg` | App icon master, 32 px and up. |
| `app_icon_small.svg` | App icon at 16 and 24 px: no pen (it turns to noise below 32 px), larger page, solid lines on the 16 px grid. |
| `pdf_file.svg` | `.pdf` file-type icon master, 32 px and up: the same page without the tile, grey outline, red band with "PDF" drawn as strokes (no font). |
| `pdf_file_small.svg` | File-type icon at 16 and 24 px: no letters, solid red band and two lines on the 16 px grid. |

`scripts/convert_icons.ps1` renders every PNG and ICO of the project from these files (desktop
app, installer, MSIX logos, Android launcher and store images, site logo and favicons). Run it
again after a change to an SVG and commit the outputs.
