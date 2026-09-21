# Vector IR and Rendering Contract

## Goals

The IR must be:
- immutable after construction;
- deterministic;
- independent of WPF/Direct2D/PDFium;
- replayable at arbitrary viewport transforms;
- bounds-aware where possible;
- serializable for diagnostics/tests (not necessarily a public storage format);
- capable of representing PDF graphics semantics without premature rasterization.

## Core structures

Conceptual model:

```csharp
public sealed record PdfDisplayList(
    PdfSize PageSize,
    IReadOnlyList<PdfDrawCommand> Commands,
    PdfFeatureSet Features);

public abstract record PdfDrawCommand(PdfRect? Bounds);

public sealed record SaveState() : PdfDrawCommand(null);
public sealed record RestoreState() : PdfDrawCommand(null);
public sealed record ConcatTransform(PdfMatrix Matrix) : PdfDrawCommand(null);
public sealed record FillPath(PdfPath Path, PdfPaint Paint, PdfFillRule Rule, PdfRect Bounds)
    : PdfDrawCommand(Bounds);
public sealed record StrokePath(PdfPath Path, PdfStroke Stroke, PdfPaint Paint, PdfRect Bounds)
    : PdfDrawCommand(Bounds);
public sealed record PushClip(PdfPath Path, PdfFillRule Rule, PdfRect Bounds)
    : PdfDrawCommand(Bounds);
public sealed record DrawGlyphRun(PdfGlyphRun Run, PdfPaint Paint, PdfRect Bounds)
    : PdfDrawCommand(Bounds);
public sealed record DrawImage(PdfImageRef Image, PdfMatrix Transform, PdfRect Bounds)
    : PdfDrawCommand(Bounds);
public sealed record DrawFallbackRegion(PdfFallbackToken Token, PdfRect Bounds)
    : PdfDrawCommand(Bounds);
```

These are illustrative contracts, not copy/paste requirements.

## Text rule

Rendering data and semantic Unicode data must be related but separate.

Preserve:
- font resource identity;
- glyph IDs/codes;
- glyph positions;
- text matrix;
- character/glyph spacing;
- horizontal scaling;
- rise;
- rendering mode;
- fill/stroke paints.

Separately maintain mapping to Unicode for:
- selection;
- search;
- copy;
- accessibility;
- comparison.

Do not reconstruct PDF rendering by laying out extracted Unicode with a normal UI text-layout engine.

## Graphics state

The interpreter maintains a stack containing at least:
- CTM;
- clipping state;
- stroke width/cap/join/miter/dash;
- fill/stroke color spaces and colors;
- alpha constants;
- blend mode;
- soft-mask reference;
- text state where applicable.

Malformed `q/Q` nesting must not crash the process; apply bounded error recovery or fail the page according to parser policy.

## Paths

Support:
- `m`, `l`, `c`, `v`, `y`, `h`, `re`
- stroke/fill combinations
- non-zero and even-odd fill
- clipping (`W`, `W*`)

Do not flatten Bézier curves into pixel-resolution polylines in the IR.

## View transform

Page-space remains stable. Rendering receives a transform:

```text
device = viewport * pageTransform * objectTransform * point
```

Zoom, DPI, rotation, and viewport translation belong in render-time transforms, not by rebuilding page geometry.

## Culling

Commands should expose conservative bounds. Renderer may skip commands that do not intersect the device-space dirty/viewport region. Unknown bounds => render, never incorrectly cull.

## Fallback command

Fallback is first-class:

```text
DrawFallbackRegion {
  token,
  bounds,
  reason,
  resource dependencies
}
```

The token must contain enough stable identity to ask a fallback provider for a bounded surface without leaking PDFium types into the IR.

## Backend contract

Conceptual:

```csharp
public interface IPdfVectorRenderer
{
    ValueTask RenderAsync(
        IPdfDisplayList page,
        IPdfVectorRenderTarget target,
        PdfRenderTransform transform,
        PdfRect dirtyRegion,
        CancellationToken cancellationToken);
}
```

Backend resources are cacheable by document/page/resource identity but must be released under memory pressure/device loss.

## Direct2D/DirectWrite mapping

- PDF path → Direct2D geometry
- fill/stroke → Direct2D brush + stroke style
- clip → Direct2D layer/clip mechanism
- glyph run → DirectWrite font face/glyph run rendered through Direct2D
- bitmap image → WIC/Direct2D bitmap
- gradient/shading → native brush when semantics match; otherwise specialized shader/intermediate surface
- complex transparency → bounded intermediate target

## Device loss

Windows rendering code must handle device loss as a recoverable event:
1. discard device-dependent resources;
2. retain document/display-list state;
3. recreate device;
4. replay visible content.

A GPU reset must not require reparsing the document.
