# PdfEngine.Vector

A PDF engine in pure C#, with no native code: parser and repair, document model, content stream
interpreter (display list), text extraction and search, annotations, forms, digital signatures,
redaction, page organisation and saving. It targets `net9.0` and `net10.0`.

It is the engine of [PDF Viewer](https://github.com/ramanacr/pdf-viewer). To draw pages, add a
renderer: [PdfEngine.Vector.Skia](https://www.nuget.org/packages/PdfEngine.Vector.Skia) (portable).

```csharp
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;

using var doc = await PdfVectorDocument.OpenAsync("input.pdf");
Console.WriteLine($"{doc.PageCount} pages");
Console.WriteLine(await new PdfVectorTextService().ExtractPageTextAsync(doc, 1));
```

## Colour profiles

The engine reads no colour profile from the machine, so it converts colour the same way on every
platform. DeviceCMYK uses a built-in model of U.S. Web Coated (SWOP). To use installed profiles,
register a provider once at start-up, before documents are opened:

```csharp
using PdfEngine.Vector.Color;

ColorProfiles.Register(new MyColorProfiles()); // implements IColorProfileProvider
```

`GetDisplayCmykProfile` gives the profile for DeviceCMYK on screen (null: the built-in model).
`GetOutputIntentCmykProfile` gives the CMYK profile that PDF/A conversion embeds as the output intent
when the caller sets no `CmykProfile` (null: none). The Windows desktop app registers the profiles
Windows and Adobe applications install.

**Behaviour change on Windows.** Earlier builds found these profiles themselves on Windows. A Windows
host that registers no provider now gets the built-in SWOP model for display, and PDF/A conversion adds
no RSWOP.icm output intent by itself. To keep the earlier results, register a Windows provider once at
start-up. In the repository, `PdfEngine.Vector.Windows` has one (it is not a NuGet package; its
`WindowsColorProfileProvider` is about 20 lines to copy):

```csharp
ColorProfiles.Register(PdfEngine.Vector.Windows.WindowsColorProfileProvider.Instance);
```

The API is not stable before version 1.0. Licence: MIT. Third-party data inside the package (the
Adobe predefined CJK CMaps, BSD-3-Clause) is listed in `THIRD_PARTY_NOTICES.md` in the package.
