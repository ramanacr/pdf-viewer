# Current → Target Architecture

## Current

```text
WPF PdfViewer (.NET 9)
        |
PdfViewer.Core
        |
PdfEngine.Abstractions
        |
PdfEngine.Pdfium
        |
pdfium.dll
        |
BGRA page buffers -> WPF BitmapSource
```

The existing architecture already has the right seam: engine contracts are separated from the UI.

## Transitional target

```text
                          PdfViewer
                              |
                       PdfViewer.Core
                              |
                    PdfEngine.Abstractions
                              |
             +----------------+----------------+
             |                                 |
     PdfEngine.Vector                    PdfEngine.Pdfium
        PRIMARY                       FALLBACK / ORACLE
             |
    +--------+---------+
    |                  |
 PdfCore            DisplayList
    |                  |
 parser               +-----------------------+
 resolver             |                       |
 resources       Windows.Vector          RasterFallback
 interpreter     Direct2D/DirectWrite          |
                        |                 Pdfium adapter
                        +----------+------------+
                                   |
                              composed page
```

## Long-term target

```text
PDF bytes
   |
PdfCore
   |
Document model
   |
Content interpreter
   |
Immutable DisplayList
   |
Windows Vector Backend
   |
Direct2D + DirectWrite (+ D3D/DXGI where justified)
```

No PDFium runtime is required in the final package.

## Architectural boundaries

### `PdfEngine.Abstractions`
Public/UI-neutral contracts only. Add capability contracts without Direct2D, WPF, COM, or PDFium types.

### `PdfEngine.Vector`
Managed orchestration plus PDF core/IR. The first implementation can remain C# to integrate safely with the existing repo. Native Windows rendering is isolated behind a backend interface. If profiling later justifies C++ for parser/renderer hot paths, introduce it behind a stable C ABI rather than rewriting the application.

### `PdfEngine.Vector.Windows`
Recommended separate Windows-specific project if Direct2D/DirectWrite interop becomes substantial. This prevents Windows COM/rendering types from leaking into the core engine.

### `PdfEngine.Pdfium`
Unchanged initially. It becomes:
1. compatibility fallback,
2. reference renderer in tests,
3. implementation source for features not yet migrated.

## Ownership rule

The new engine must own:
- PDF object identity;
- parsed page/resource model;
- display list;
- feature/capability classification;
- fallback decision.

PDFium must **not** decide the new engine's canonical page structure.

## Rendering integration

Do not delete `RenderedPage`/bitmap contracts immediately. Add a vector-capable path beside them. The viewer can use bitmap rendering for pages/features not yet migrated while vector-capable page controls are introduced incrementally.

A recommended abstraction:

```csharp
public enum PdfRenderMode
{
    Auto,
    VectorPreferred,
    Raster
}

public interface IPdfPageVisual
{
    PdfSize Size { get; }
    PdfRenderCapabilities Capabilities { get; }
}

public interface IPdfVectorPageVisual : IPdfPageVisual
{
    IPdfDisplayList DisplayList { get; }
}
```

The actual names may be adapted to existing conventions after inspecting current interfaces. Do not create duplicate concepts when an equivalent already exists.
