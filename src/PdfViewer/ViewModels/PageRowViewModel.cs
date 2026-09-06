using System.Collections.Generic;

namespace PdfViewer.ViewModels;

/// <summary>
/// One row of pages as drawn: a single page in continuous mode, a facing pair in two-page
/// mode, and one page on its own for a cover or a trailing odd page.
///
/// The view binds to these rather than to the flat page list, so the grouping the user sees
/// is literally the grouping the scroll maths uses. An earlier version let the panel wrap the
/// pages itself, which quietly paired the cover with page 1 while the maths had it standing
/// alone - every scroll target after the cover was then a row out.
/// </summary>
public sealed class PageRowViewModel
{
    public PageRowViewModel(IReadOnlyList<PageViewModel> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>1-based page number of the first page in the row.</summary>
    public int FirstPageNumber => Pages.Count > 0 ? Pages[0].PageNumber : 0;
}
