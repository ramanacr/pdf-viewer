using Android.App;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// Search options and the results list (the search bar itself is built in MainActivity.cs): match
/// case and whole word, which search again when switched, and a list of every match with its page
/// and the text around it (the match in bold); a tap goes to that match. The matches stay
/// highlighted on the pages while the list is open and after.
/// </summary>
public sealed partial class MainActivity
{
    private bool _matchCase, _wholeWord;
    private CheckBox _matchCaseToggle = null!, _wholeWordToggle = null!;
    private TextView _resultsButton = null!;

    /// <summary>A search option as a 48 dp toggle with a short label; TalkBack reads it as a check box with its full name.</summary>
    private CheckBox SearchToggle(string label, string description, Action<bool> changed)
    {
        var toggle = new CheckBox(this) { Text = label, ContentDescription = description, Gravity = GravityFlags.Center };
        toggle.SetButtonDrawable((global::Android.Graphics.Drawables.Drawable?)null);
        toggle.SetMinWidth((int)Dp(48));
        toggle.SetMinHeight((int)Dp(48));
        toggle.SetPadding((int)Dp(10), 0, (int)Dp(10), 0);
        toggle.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        toggle.CheckedChange += (_, e) =>
        {
            changed(e.IsChecked);
            ShowSearchOptions();
            if (!string.IsNullOrWhiteSpace(_searchText.Text)) _ = SearchAsync(_searchText.Text ?? "");
        };
        return toggle;
    }

    /// <summary>The search bar's second row: the match count, the options and the results list.</summary>
    private LinearLayout BuildSearchOptions()
    {
        var row = Bar();
        row.SetPadding((int)Dp(4), 0, (int)Dp(4), 0);
        _matchCaseToggle = SearchToggle("Aa", "Match case", on => _matchCase = on);
        _wholeWordToggle = SearchToggle("|ab|", "Whole words only", on => _wholeWord = on);
        _resultsButton = ToolButton("List", "List all matches", (_, _) => ShowSearchResults());
        _searchCount.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        _searchCount.SetMaxLines(2);
        // TalkBack says the count when a search ends or the match changes.
        _searchCount.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        row.AddView(_searchCount, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(_matchCaseToggle);
        row.AddView(_wholeWordToggle);
        row.AddView(_resultsButton);
        return row;
    }

    /// <summary>The options' colours for the theme and their state: a switched-on option is tinted.</summary>
    private void ShowSearchOptions()
    {
        foreach (var toggle in new[] { _matchCaseToggle, _wholeWordToggle })
        {
            toggle.SetTextColor(toggle.Checked ? AccentColor : TextColor);
            toggle.SetBackgroundColor(toggle.Checked ? new Color(AccentColor.R, AccentColor.G, AccentColor.B, (byte)48) : Color.Transparent);
            toggle.SetTypeface(null, toggle.Checked ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        _resultsButton.SetTextColor(AccentColor);
        _resultsButton.Enabled = _hits.Count > 0;
        _resultsButton.Alpha = _hits.Count > 0 ? 1f : 0.4f;
    }

    /// <summary>Every match: page, then the text around it with the match in bold. The current one is marked; a tap shows that one.</summary>
    private void ShowSearchResults()
    {
        if (_hits.Count == 0) return;
        var hits = _hits;
        var items = new Java.Lang.ICharSequence[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            string head = $"{(i == _hitIndex ? "▸ " : "")}Page {hit.Page}   ";
            var text = new SpannableStringBuilder();
            text.Append(head);
            text.SetSpan(new ForegroundColorSpan(AccentColor), 0, head.Length, SpanTypes.ExclusiveExclusive);
            text.Append(hit.Before);
            int start = text.Length();
            text.Append(hit.Match);
            text.SetSpan(new StyleSpan(TypefaceStyle.Bold), start, text.Length(), SpanTypes.ExclusiveExclusive);
            text.Append(hit.After);
            items[i] = text;
        }
        var dialog = new AlertDialog.Builder(this)
            .SetTitle(hits.Count == 1 ? "1 match" : $"{hits.Count} matches")!
            .SetItems(items, (_, e) =>
            {
                if (!ReferenceEquals(hits, _hits) || e.Which < 0 || e.Which >= hits.Count) return;
                _hitIndex = e.Which;
                ShowHit();
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Create()!;
        dialog.Show();
        // Opens at the current match.
        if (_hitIndex > 0) dialog.ListView?.SetSelection(_hitIndex);
    }
}
