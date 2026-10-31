using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Widget;
using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Links on the pages: a tap on a link to a place in the document goes there (the system Back
/// returns), a web link opens in the browser after the reader agrees, and anything else (another
/// file, a program, document JavaScript) is never followed. The links and their targets come from
/// the engine layer the web viewer shares (ViewerLinks).
/// </summary>
public sealed partial class MainActivity
{
    private readonly ViewerHistory<(PageAnchor TopLeft, double Zoom)> _linkBack = new();

    private void OnLinkTapped(ViewerLink link)
    {
        if (_doc is not { } doc) return;
        var target = link.Target;
        switch (target.Kind)
        {
            case PdfLinkTargetKind.WebUri when PdfLinkTargets.TryGetWebUri(target.Uri, out var uri):
                AskToOpen(uri);
                break;
            case PdfLinkTargetKind.WebUri:
            case PdfLinkTargetKind.BlockedUri:
                new AlertDialog.Builder(this)
                    .SetTitle("Link not opened")!
                    .SetMessage($"Only web (http, https) and email links open from a document. This link points to:\n\n{target.Uri}")!
                    .SetPositiveButton("OK", (_, _) => { })!
                    .Show();
                break;
            default:
                if (target.IsInternal && ViewerLinks.Destination(target, _pages.CurrentPage, doc.Geometry) is { } location)
                {
                    if (_pages.SavedView is { } view) _linkBack.Push(view);
                    _pages.GoToLocation(location);
                    _pages.AnnounceForAccessibility(target.Kind == PdfLinkTargetKind.Page ? target.Description : $"Page {location.Page}");
                }
                else
                {
                    Toast.MakeText(this, target.Description, ToastLength.Short)?.Show();
                }
                break;
        }
    }

    /// <summary>Back after following a link: the view it was followed from. False when there is none.</summary>
    private bool GoBackByLink()
    {
        if (_doc == null || !_linkBack.TryPop(out var view)) return false;
        _pages.RestoreView(view.TopLeft, view.Zoom);
        return true;
    }

    /// <summary>"Open this link in your browser?" with the full URL and its site in bold; opens it only on Open.</summary>
    private void AskToOpen(System.Uri uri)
    {
        bool mail = uri.Scheme == System.Uri.UriSchemeMailto;
        string host = PdfLinkTargets.DisplayHost(uri);
        var message = new SpannableStringBuilder();
        message.Append(mail ? "To: " : "Site: ");
        int start = message.Length();
        message.Append(host);
        message.SetSpan(new StyleSpan(TypefaceStyle.Bold), start, message.Length(), SpanTypes.ExclusiveExclusive);
        message.Append("\n\n" + uri.OriginalString + "\n\nThe link comes from the document. Open it only if you trust where it goes.");
        new AlertDialog.Builder(this)
            .SetTitle(mail ? "Write an email?" : "Open this link in your browser?")!
            .SetMessage(message)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton(mail ? "Write email" : "Open link", (_, _) => Open(uri))!
            .Show();
    }

    private void Open(System.Uri uri)
    {
        // Checked again: only an absolute http, https or mailto URI leaves the app.
        if (!PdfLinkTargets.TryGetWebUri(uri.OriginalString, out var safe)) return;
        try
        {
            var intent = new Intent(Intent.ActionView, Uri.Parse(safe.AbsoluteUri));
            intent.AddCategory(Intent.CategoryBrowsable);
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "No app on this device can open this link.", ToastLength.Short)?.Show();
        }
    }
}
