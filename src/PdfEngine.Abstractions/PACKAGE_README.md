# PdfEngine.Abstractions

The UI-neutral contracts and models of the PdfEngine PDF engine: documents, pages, geometry,
render requests and rendered pages, text segments and search, annotations, forms and save
options. It has no dependencies.

You normally get it through [PdfEngine.Vector](https://www.nuget.org/packages/PdfEngine.Vector)
(the engine) and [PdfEngine.Vector.Skia](https://www.nuget.org/packages/PdfEngine.Vector.Skia) (a
portable renderer). Reference it alone to write code against the contracts only.

Source and documentation: https://github.com/ramanacr/pdf-viewer. The API is not stable before
version 1.0. Licence: MIT.
