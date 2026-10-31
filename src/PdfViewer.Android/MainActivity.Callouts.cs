using Android.App;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// Callouts and moving: the callout tool (a tap on the spot it points to, a tap where its text box
/// goes, then its text), and "Move" for the chosen annotation (a tap says where it goes; a callout
/// asks whether its text box or the spot it points to moves). The rules are the shared
/// <see cref="ViewerEditSession"/>'s (<see cref="ViewerEditSession.AddCallout"/>,
/// <see cref="ViewerEditSession.Move"/>, <see cref="ViewerEditSession.MoveCalloutAnchor"/>): each
/// change is one undo step, a callout's text follows the text box rules, and moving its box keeps
/// the spot it points to.
/// </summary>
public sealed partial class MainActivity
{
    private TextView _moveButton = null!;

    /// <summary>What the place tool does with the next tap (moving the chosen annotation), or null.</summary>
    private Action<int, ViewerPoint>? _placing;

    private TextView CalloutToolButton() =>
        ToolButton("Callout", "Callout: tap the spot it points to, then where its text goes", (_, _) => SetTool(PageView.PageTool.Callout));

    private void BuildMoveButton() =>
        _moveButton = ToolButton("Move", "Move the chosen annotation: then tap where it goes", (_, _) => MovePicked());

    private void ConnectCallouts()
    {
        _pages.CalloutRequested += OnCalloutTap;
        _pages.PlaceRequested += (page, point) =>
        {
            var place = _placing;
            _placing = null;
            _pages.Tool = PageView.PageTool.Browse;
            UpdateEditButtons();
            place?.Invoke(page, point);
        };
    }

    /// <summary>
    /// A tap with the callout tool: the first (or one on another page) is the spot the callout
    /// points to, marked on the page; the next one is where its text box goes, and the text is asked.
    /// </summary>
    private void OnCalloutTap(int page, ViewerPoint point)
    {
        if (_pages.CalloutMark is not var (markPage, anchor) || markPage != page)
        {
            _pages.CalloutMark = (page, point);
            Toast.MakeText(this, "Now tap where the callout's text goes.", ToastLength.Short)?.Show();
            return;
        }
        SetTool(PageView.PageTool.Browse);
        AskText("New callout", "", ViewerAnnotationStore.MaxContentsLength, multiline: true, password: false, text =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Toast.MakeText(this, "A callout needs text.", ToastLength.Short)?.Show();
                return;
            }
            // A tap, not a drag: the box is as wide as a text box and as tall as its text.
            Change(s => Picked(s.AddCallout(page, anchor, new ViewerRect(point.X, point.Y, 0, 0), text).Id));
        });
    }

    /// <summary>"Move": the next tap says where the chosen annotation goes; a callout's text box or the spot it points to.</summary>
    private void MovePicked()
    {
        if (_pages.PickedAnnotation is not { } id || _session?.Store.Find(id) is not { } a || a.Kind == ViewerAnnotationKind.Reply) return;
        void PlaceBox() => StartPlacing("Tap where its top-left corner goes.", (page, point) =>
        {
            if (!SamePage(a, page) || _session?.Store.Find(id) is not { } now) return;
            var shown = _session.Document.Geometry[now.Page - 1].ToShown(now.Box);
            Change(s => s.Move(id, point.X - shown.X, point.Y - shown.Y));
            Pick(id);
        });
        void PlaceAnchor() => StartPlacing("Tap the spot the callout points to.", (page, point) =>
        {
            if (!SamePage(a, page)) return;
            Change(s => s.MoveCalloutAnchor(id, point));
            Pick(id);
        });
        if (!a.IsCallout)
        {
            PlaceBox();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Move the callout")!
            .SetItems(new[] { "Move its text box", "Move the spot it points to" }, (_, e) =>
            {
                if (e.Which == 0) PlaceBox();
                else PlaceAnchor();
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private bool SamePage(ViewerAnnotation a, int page)
    {
        if (page == a.Page) return true;
        Toast.MakeText(this, "Tap on the page the annotation is on.", ToastLength.Short)?.Show();
        return false;
    }

    private void StartPlacing(string hint, Action<int, ViewerPoint> place)
    {
        _placing = place;
        // The chosen annotation stays chosen (SetTool would clear it).
        _pages.Tool = PageView.PageTool.Place;
        UpdateEditButtons();
        Toast.MakeText(this, hint, ToastLength.Short)?.Show();
    }
}
