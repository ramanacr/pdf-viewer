# PdfEngine.Vector

A PDF engine in pure C#, with no native code: parser and repair, document model, content stream
interpreter (display list), text extraction and search, annotations, forms, digital signatures,
redaction, page organisation and saving. It targets `net9.0`.

It is the engine of [PDF Viewer](https://github.com/ramanacr/pdf-viewer). To draw pages, add a
renderer: [PdfEngine.Vector.Skia](https://www.nuget.org/packages/PdfEngine.Vector.Skia) (portable).

```csharp
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;

using var doc = await PdfVectorDocument.OpenAsync("input.pdf");
Console.WriteLine($"{doc.PageCount} pages");
Console.WriteLine(await new PdfVectorTextService().ExtractPageTextAsync(doc, 1));
```

The API is not stable before version 1.0. Licence: MIT. Third-party data inside the package (the
Adobe predefined CJK CMaps, BSD-3-Clause) is listed in `THIRD_PARTY_NOTICES.md` in the package.
