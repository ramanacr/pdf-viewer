# Decisions and Scope

## Decision summary

### Chosen approach
**Approach 2:** build our own PDF parser/document model/content interpreter/vector IR/native renderer while retaining PDFium as a temporary fallback and reference implementation.

### Destination
**Approach 3:** our PDF core + our renderer, with no mandatory PDFium production dependency.

### Why
A direct jump to a fully independent engine creates excessive compatibility risk. A PDFium-first architecture, however, makes PDFium the canonical interpreter and makes later removal expensive. The chosen path makes **our representation canonical from day one**, while PDFium provides a controlled safety net.

## In scope

- PDF lexical/syntactic parser and indirect-object resolver
- classic xref and xref streams
- object streams
- page tree/resource inheritance
- content-stream interpreter
- graphics-state machine
- immutable page display list / vector IR
- vector paths, transforms, clipping
- glyph-position-preserving text representation
- images and basic color spaces
- Direct2D/DirectWrite Windows backend
- localized raster fallback abstraction
- PDFium compatibility adapter/fallback
- differential rendering/testing
- telemetry for unsupported constructs
- memory/performance/security limits
- staged migration of current rendering path

## Initially out of scope

These remain on PDFium/current services until separately migrated:
- full annotation editing/writing implementation
- AcroForm editing
- signatures and CMS verification replacement
- redaction writing
- document restructuring/save engine
- every PDF 2.0 interactive feature
- JavaScript
- multimedia
- 3D/RichMedia
- replacement of WPF
- cross-platform renderer

The engine must be designed so these can move later without architectural rework.

## Definition of vector-first

A page is parsed into commands such as:

```text
SaveGraphicsState
ConcatMatrix
SetFillColor
MoveTo / LineTo / CurveTo / ClosePath
FillPath / StrokePath
PushClip
DrawGlyphRun
DrawImage
BeginTransparencyGroup
EndTransparencyGroup
RestoreGraphicsState
```

The viewport transform is applied at render time. A zoom change does not require regenerating a full-page raster at the new resolution.

## Rasterization policy

Rasterization is permitted for:
- embedded bitmap/image content;
- PDF semantics that require an intermediate compositing surface;
- bounded fallback regions during migration;
- print/export paths where raster output is explicitly requested.

Rasterization of an entire ordinary text/vector page merely because the UI zoom changed is prohibited in the vector path.

## Compatibility policy

Correctness beats “pure vector” ideology. If a PDF feature cannot yet be rendered faithfully, route it through an explicit fallback rather than silently producing incorrect output.

## UI policy

Keep the current WPF viewer during this migration. Replacing WPF/.NET is a separate initiative because simultaneous UI and engine replacement would make regression attribution substantially harder.
