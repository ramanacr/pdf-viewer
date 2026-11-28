using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;

namespace PdfViewer.Android;

/// <summary>
/// The Windows app's keyboard shortcuts, for a hardware keyboard (a Chromebook, a tablet's keyboard
/// cover, a phone with a Bluetooth keyboard): docs/features.md, Keyboard shortcuts. A key that has
/// no meaning at the moment (no document, no selection, no match) does what Android does with it.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>Views Back left (Alt+Right goes forward to them); a new jump clears them, as on Windows.</summary>
    private readonly Stack<(PageAnchor TopLeft, double Zoom)> _jumpForward = new();

    /// <summary>A shortcut of the Windows app; true when the key was one and did something.</summary>
    private bool HandleShortcut(Keycode key, KeyEvent? e)
    {
        if (e == null) return false;
        bool ctrl = e.IsCtrlPressed, shift = e.IsShiftPressed, alt = e.IsAltPressed;
        bool open = _doc != null;
        // Typing in the search box keeps its keys, except the search keys themselves.
        bool typing = _searchText.HasFocus;
        switch (key)
        {
            case Keycode.O when ctrl:
                PickDocument();
                return true;
            case Keycode.S when ctrl && open:
                if (shift) PickSaveCopy();
                else if (_session?.IsDirty == true) Save();
                else Toast.MakeText(this, "There are no changes to save.", ToastLength.Short)?.Show();
                return true;
            case Keycode.P when ctrl && open:
                Print();
                return true;
            case Keycode.F when ctrl && open:
                ShowSearch(_searchBar.Visibility != ViewStates.Visible);
                return true;
            case Keycode.F3 when open && _hits.Count > 0:
                StepHit(shift ? -1 : 1);
                return true;
            case Keycode.Enter when typing:
                return false;
            case Keycode.C when ctrl && !typing && _pages.HasSelection:
                CopySelection();
                return true;
            case Keycode.A when ctrl && !typing && open:
                _pages.SelectPage();
                return true;
            case Keycode.Z when ctrl && !typing && _session?.CanUndo == true:
                Change(s => s.Undo());
                return true;
            case Keycode.Y when ctrl && !typing && _session?.CanRedo == true:
                Change(s => s.Redo());
                return true;
            case Keycode.D when ctrl && open:
                ShowProperties();
                return true;
            case Keycode.E when ctrl && open:
                EditTextAndImages();
                return true;
            case Keycode.Num1 when ctrl && open:
                _pages.ZoomTo(1);
                return true;
            case Keycode.Num4 when ctrl && open:
                ShowReadMode();
                return true;
            case Keycode.B when ctrl && open:
                ShowThumbnails(_thumbnails.Visibility != ViewStates.Visible);
                return true;
            case Keycode.N when ctrl && shift && open:
                _pages.NightPages = !_pages.NightPages;
                return true;
            case Keycode.W when ctrl && open:
                CloseDocument();
                return true;
            case Keycode.F11 when open:
                SetFullScreen(!_fullScreen);
                return true;
            case Keycode.DpadLeft when alt && open:
                return GoBackByLink();
            case Keycode.DpadRight when alt && open:
                return GoForward();
            case Keycode.PageDown when open && !typing:
                JumpToPage(Math.Min(_doc!.PageCount, _pages.CurrentPage + 1), record: false);
                return true;
            case Keycode.PageUp when open && !typing:
                JumpToPage(Math.Max(1, _pages.CurrentPage - 1), record: false);
                return true;
            case Keycode.MoveHome when open && !typing:
                JumpToPage(1);
                return true;
            case Keycode.MoveEnd when open && !typing:
                JumpToPage(_doc!.PageCount);
                return true;
            case Keycode.ForwardDel when open && !typing && _pages.PickedAnnotation != null && CanAnnotate:
                DeletePicked();
                return true;
            // The chosen annotation, as on Windows: arrows nudge it 1 point (Shift: 10), Enter opens its
            // comment, Esc lets go of it; Tab and Shift+Tab choose the next and previous annotation.
            case Keycode.DpadLeft or Keycode.DpadRight or Keycode.DpadUp or Keycode.DpadDown
                when open && !typing && !alt && CanAnnotate && _pages.PickedAnnotation is { } nudged:
            {
                double step = shift ? 10 : 1;
                double dx = key == Keycode.DpadLeft ? -step : key == Keycode.DpadRight ? step : 0;
                double dy = key == Keycode.DpadUp ? -step : key == Keycode.DpadDown ? step : 0;
                // The arrows go the way the screen shows; the view may be turned.
                var (px, py) = _pages.ViewRotation switch
                {
                    90 => (dy, -dx),
                    180 => (-dx, -dy),
                    270 => (-dy, dx),
                    _ => (dx, dy),
                };
                Change(sess => sess.Move(nudged, px, py));
                return true;
            }
            case Keycode.Tab when open && !typing && _session is { } tabbing:
            {
                var list = tabbing.Store.Current.Where(x => x.Kind != Web.Engine.Annotations.ViewerAnnotationKind.Reply && !x.IsStatus)
                    .Select((x, i) => (Annotation: x, Index: i)).OrderBy(x => x.Annotation.Page).ThenBy(x => x.Index).Select(x => x.Annotation).ToList();
                if (list.Count == 0) return false;
                int at = list.FindIndex(x => x.Id == _pages.PickedAnnotation);
                int next = at < 0 ? (shift ? list.Count - 1 : 0) : (at + (shift ? -1 : 1) + list.Count) % list.Count;
                var chosen = list[next];
                if (CanAnnotate && _annotateBar.Visibility != ViewStates.Visible) ShowAnnotateBar(true);
                Pick(chosen.Id);
                _pages.GoToAnnotation(chosen);
                return true;
            }
            case Keycode.Enter or Keycode.NumpadEnter when open && !typing && _pages.PickedAnnotation is { } opened:
                ShowThread(opened);
                return true;
            case Keycode.Escape when _pages.PickedAnnotation != null && _annotateBar.Visibility == ViewStates.Visible:
                Pick(null);
                return true;
            case Keycode.Escape:
                // Esc does what Back does inside the app (selection, search, a tool, full screen), but never leaves it.
                return HandleBack(inAppOnly: true);
        }
        return false;
    }

    /// <summary>Alt+Right: forward again to the view Back left. False when there is none.</summary>
    private bool GoForward()
    {
        if (_doc == null || _jumpForward.Count == 0) return false;
        if (_pages.SavedView is { } here) _linkBack.Push(here);
        var (topLeft, zoom) = _jumpForward.Pop();
        _pages.RestoreView(topLeft, zoom);
        return true;
    }

    /// <summary>
    /// Ctrl+W and ⋮ → Close document: back to the start screen. Unsaved edits ask first (save,
    /// discard or cancel), as when another file is opened.
    /// </summary>
    private void CloseDocument() => AfterDiscard(() =>
    {
        if (_doc is not { } doc) return;
        RememberPosition();
        StopReadingAloud(null);
        CloseToolOverlay();
        ShowSearch(false);
        _pages.ClearSelection();
        _doc = null;
        _uri = null;
        _fileName = null;
        _password = null;
        _linkBack.Clear();
        _jumpForward.Clear();
        _pages.SetDocument(null);
        StartEdits(null);
        _thumbnails.Release();
        if (_fullScreen) SetFullScreen(false);
        // The window shows no document now (MainActivity.Windows.cs): Recents and Documents forget it.
        Shown().Remove(TaskId);
        SaveShown();
        SetTaskLabel(null);
        ShowDocumentState();
        _ = doc.DisposeAsync().AsTask();
    });
}
