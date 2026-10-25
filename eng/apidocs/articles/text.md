# Text extraction and search

[`PdfVectorTextService`](xref:PdfEngine.Vector.Text.PdfVectorTextService) implements
[`IPdfTextService`](xref:PdfEngine.Text.IPdfTextService) on a `PdfVectorDocument`.

```csharp
using PdfEngine.Text;
using PdfEngine.Vector.Text;

var text = new PdfVectorTextService();

string page1 = await text.ExtractPageTextAsync(doc, 1);

IReadOnlyList<TextSegment> words = await text.ExtractTextSegmentsAsync(doc, 1);

IReadOnlyList<SearchMatch> matches = await text.SearchTextAsync(
    doc, "invoice", new SearchOptions { MatchCase = false, MatchWholeWord = true, MaxResults = 100 });
```

## Page text

`ExtractPageTextAsync` returns the text of one page as one string. A page number out of range
gives an empty string.

## Words and positions

`ExtractTextSegmentsAsync` returns one [`TextSegment`](xref:PdfEngine.Text.TextSegment) per word.

The coordinates are normalized:

- `X` and `Width` are fractions of the displayed page width (0 is the left edge, 1 the right edge).
- `Y` and `Height` are fractions of the displayed page height (0 is the top edge).
- "Displayed" means after the page's `/Rotate`.
- The boxes have a small padding, so that a highlight drawn from them covers the glyphs.

To get pixels of a rendered page, multiply by the page's pixel size:

```csharp
double left = segment.X * page.WidthPixels;
double top = segment.Y * page.HeightPixels;
```

## Search

`SearchTextAsync` searches every page in order and stops after
[`SearchOptions.MaxResults`](xref:PdfEngine.Text.SearchOptions) matches (default 1000). Each
[`SearchMatch`](xref:PdfEngine.Text.SearchMatch) has the page number, the normalized bounds, the
character index on the page and a context snippet for a result list.

- An empty query finds nothing.
- `MatchCase` and `MatchWholeWord` are off by default.
- Pass a `CancellationToken` to stop a long search.

## Documents of other engines

The service reads text from a `PdfVectorDocument`. For an `IPdfDocument` of another engine, call
`Attach(document, vectorDocument)` with a vector document of the same file. Otherwise the service
opens the file from `IPdfDocument.FilePath`.
