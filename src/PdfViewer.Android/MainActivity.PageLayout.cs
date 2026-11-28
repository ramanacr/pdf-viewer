using Android.Content;
using Android.Text;
using Android.Text.Style;
using Java.Lang;
using PdfViewer.Web.Engine;

namespace PdfViewer.Android;

/// <summary>
/// ⋮ → View → Page layout: the desktop's view modes. Continuous (the default), one page at a time
/// (fitted to the screen, a swipe turns the page), two pages side by side, and two pages with the
/// cover alone so that spreads line up as in the printed book. The choice is kept in the app's
/// preferences and in the saved state, and applies to the open document and the next ones.
/// </summary>
public sealed partial class MainActivity
{
    private const string ViewPreferences = "view", ViewModeKey = "view-mode";

    /// <summary>The layouts in the dialog's order, with the value kept for each.</summary>
    private static readonly (string Value, PageLayoutMode Mode, bool Cover)[] PageLayouts =
    {
        ("continuous", PageLayoutMode.Continuous, false),
        ("single", PageLayoutMode.SinglePage, false),
        ("facing", PageLayoutMode.Facing, false),
        ("facing-cover", PageLayoutMode.Facing, true),
    };

    /// <summary>The kept value of the pages' current layout.</summary>
    private string PageLayoutValue =>
        PageLayouts.FirstOrDefault(l => l.Mode == _pages.LayoutMode && l.Cover == _pages.CoverAlone).Value ?? "continuous";

    /// <summary>Lays the pages out as a kept value says (an unknown value leaves them as they are).</summary>
    private void ApplyPageLayout(string? value)
    {
        foreach (var layout in PageLayouts)
            if (layout.Value == value) _pages.SetLayout(layout.Mode, layout.Cover);
    }

    /// <summary>The layout kept in the app's preferences, for the pages at start.</summary>
    private void ApplyStoredPageLayout() =>
        ApplyPageLayout(GetSharedPreferences(ViewPreferences, FileCreationMode.Private)?.GetString(ViewModeKey, null));

    /// <summary>The page layout dialog: each choice with a line on what it is for; a choice applies at once.</summary>
    private void AskPageLayout()
    {
        // Two pages side by side are small on a phone held upright: allowed, with a word on where they fit best.
        var config = Resources?.Configuration;
        bool narrow = config != null && config.Orientation == global::Android.Content.Res.Orientation.Portrait && config.SmallestScreenWidthDp < 600;
        string where = narrow ? "; best in landscape or on a tablet" : "";
        var items = new[]
        {
            Choice("Continuous", "Scroll through the pages in one column"),
            Choice("Single page", "One page at a time, fitted to the screen; swipe to turn"),
            Choice("Two pages (facing)", "Pages side by side" + where),
            Choice("Two pages with cover", "The cover alone, then spreads as in the printed book" + where),
        };
        int current = System.Array.FindIndex(PageLayouts, l => l.Value == PageLayoutValue);
        new AlertDialog.Builder(this)
            .SetTitle("Page layout")!
            .SetSingleChoiceItems(items, current, (sender, e) =>
            {
                (sender as IDialogInterface)?.Dismiss();
                if (e.Which < 0 || e.Which >= PageLayouts.Length) return;
                string value = PageLayouts[e.Which].Value;
                ApplyPageLayout(value);
                GetSharedPreferences(ViewPreferences, FileCreationMode.Private)?.Edit()?.PutString(ViewModeKey, value)?.Apply();
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>A dialog line: the choice's name, and below it in smaller type what it does (TalkBack reads both).</summary>
    private static ICharSequence Choice(string name, string description)
    {
        var text = new SpannableString(name + "\n" + description);
        text.SetSpan(new RelativeSizeSpan(0.85f), name.Length + 1, text.Length(), SpanTypes.ExclusiveExclusive);
        return text;
    }
}
