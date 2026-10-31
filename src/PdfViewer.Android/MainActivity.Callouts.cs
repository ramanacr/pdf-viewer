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

    /// <summary>
    /// What the place tool does with a tap (moving the chosen annotation), or null: true when it is
    /// done, false to wait for another tap (one on the wrong page).
    /// </summary>
    private Func<int, ViewerPoint, bool>? _placing;

    private HorizontalScrollView _annotateStrip = null!;
    private LinearLayout _placeHint = null!;
    private TextView _placeHintText = null!;

    /// <summary>What to tap while Move waits, and Cancel: shown in the annotation bar in place of its tools.</summary>
    private LinearLayout BuildPlaceHint()
    {
        _placeHint = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal, Visibility = global::Android.Views.ViewStates.Gone };
        _placeHint.SetGravity(global::Android.Views.GravityFlags.CenterVertical);
        _placeHintText = new TextView(this);
        _placeHintText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        _placeHintText.SetPadding((int)Dp(8), 0, (int)Dp(8), 0);
        // TalkBack reads the hint when it appears.
        _placeHintText.AccessibilityLiveRegion = global::Android.Views.AccessibilityLiveRegion.Polite;
        _placeHint.AddView(_placeHintText, new LinearLayout.LayoutParams(0, global::Android.Views.ViewGroup.LayoutParams.WrapContent, 1f));
        _placeHint.AddView(ToolButton("Cancel", "Cancel the move", (_, _) => StopPlacing()));
        return _placeHint;
    }

    private TextView CalloutToolButton() =>
        ToolButton("Callout", "Callout: tap the spot it points to, then where its text goes", (_, _) => SetTool(PageView.PageTool.Callout));

    private void BuildMoveButton() =>
        _moveButton = ToolButton("Move", "Move the chosen annotation: then tap where it goes", (_, _) => MovePicked());

    private void ConnectCallouts()
    {
        _pages.CalloutRequested += OnCalloutTap;
        _pages.PlaceRequested += (page, point) =>
        {
            if (_placing is not { } place) return;
            // A tap on the wrong page keeps waiting (the hint and Cancel stay).
            if (place(page, point)) StopPlacing();
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
            if (!SamePage(a, page)) return false;
            if (_session?.Store.Find(id) is not { } now) return true;
            var shown = _session.Document.Geometry[now.Page - 1].ToShown(now.Box);
            Change(s => s.Move(id, point.X - shown.X, point.Y - shown.Y));
            Pick(id);
            return true;
        });
        void PlaceAnchor() => StartPlacing("Tap the spot the callout points to.", (page, point) =>
        {
            if (!SamePage(a, page)) return false;
            Change(s => s.MoveCalloutAnchor(id, point));
            Pick(id);
            return true;
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

    private void StartPlacing(string hint, Func<int, ViewerPoint, bool> place)
    {
        _placing = place;
        // The chosen annotation stays chosen (SetTool would clear it).
        _pages.Tool = PageView.PageTool.Place;
        _placeHintText.Text = hint;
        _annotateStrip.Visibility = global::Android.Views.ViewStates.Gone;
        _placeHint.Visibility = global::Android.Views.ViewStates.Visible;
        UpdateEditButtons();
    }

    /// <summary>
    /// Move no longer waits for a tap (done, cancelled, the bar closed, another document): the tools
    /// come back. With <paramref name="keepTool"/> the page tool is left as it is (another tool was chosen).
    /// </summary>
    private void StopPlacing(bool keepTool = false)
    {
        _placing = null;
        if (!keepTool && _pages.Tool == PageView.PageTool.Place) _pages.Tool = PageView.PageTool.Browse;
        if (_placeHint == null) return;
        _placeHint.Visibility = global::Android.Views.ViewStates.Gone;
        _annotateStrip.Visibility = global::Android.Views.ViewStates.Visible;
        UpdateEditButtons();
    }
}
