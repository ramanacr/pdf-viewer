using System.Globalization;
using Android.App;
using Android.Content;
using Android.Widget;
using PdfEngine.Vector.Annotations;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// The stamp tool: the reader chooses one of the engine's stamps (<see cref="ViewerEditSession.Stamps"/>:
/// Approved, Draft, Confidential and the others, and the dynamic ones that add a name and the date
/// and time), then taps the page where it goes. The shared session places it at the engine's
/// size, upright as the page is shown, and the engine draws its appearance; Delete, Undo and Redo
/// work as for the other annotations. A dynamic stamp's name is asked once and kept in this app's
/// private preferences (backups are off): it goes into the document only, never anywhere else.
/// </summary>
public sealed partial class MainActivity
{
    private const string StampPreferences = "stamps", SignerKey = "signer";

    /// <summary>The stamp the stamp tool places.</summary>
    private PdfStandardStamp? _stamp;

    /// <summary>The Stamp button: the stamp list, or back to browsing when the stamp tool is on.</summary>
    private void StampTool()
    {
        if (_pages.Tool == PageView.PageTool.Stamp)
        {
            SetTool(PageView.PageTool.Browse);
            return;
        }
        ChooseStamp();
    }

    /// <summary>"Approved", and "Approved, with name and date" for a dynamic stamp.</summary>
    private static string StampTitle(PdfStandardStamp stamp)
    {
        string title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stamp.Label.ToLowerInvariant());
        return stamp.Dynamic ? title + ", with name and date" : title;
    }

    private void ChooseStamp()
    {
        if (!CanAnnotate) return;
        var stamps = ViewerEditSession.Stamps;
        new AlertDialog.Builder(this)
            .SetTitle("Stamp")!
            .SetItems(stamps.Select(StampTitle).ToArray(), (_, e) =>
            {
                var chosen = stamps[e.Which];
                if (chosen.Dynamic && Signer() == null) AskSigner(() => ArmStamp(chosen));
                else ArmStamp(chosen);
            })!
            .SetNeutralButton("Your name…", (_, _) => AskSigner(null))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>The stamp tool is on with <paramref name="stamp"/>: the next tap on a page places it.</summary>
    private void ArmStamp(PdfStandardStamp stamp)
    {
        _stamp = stamp;
        if (_pages.Tool != PageView.PageTool.Stamp) SetTool(PageView.PageTool.Stamp);
        Toast.MakeText(this, $"Tap where the {StampTitle(stamp).Split(',')[0]} stamp goes.", ToastLength.Short)?.Show();
    }

    private void PlaceStamp(int page, ViewerPoint point)
    {
        SetTool(PageView.PageTool.Browse);
        if (_stamp is not { } stamp) return;
        string? signer = stamp.Dynamic ? Signer() : null;
        if (stamp.Dynamic && signer == null)
        {
            AskSigner(() => Change(s => s.AddStamp(page, point, stamp, Signer()) is { } a && Picked(a.Id)));
            return;
        }
        Change(s => s.AddStamp(page, point, stamp, signer) is { } a && Picked(a.Id));
    }

    /// <summary>The name dynamic stamps show, as kept in this app's preferences, or null.</summary>
    private string? Signer()
    {
        string? name = GetSharedPreferences(StampPreferences, FileCreationMode.Private)?.GetString(SignerKey, null);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>Asks for the name dynamic stamps show and keeps it on this device; <paramref name="then"/> runs when one is given.</summary>
    private void AskSigner(Action? then)
    {
        AskText("Your name on stamps (kept on this device)", Signer() ?? "", ViewerEditSession.MaxStampSignerLength, multiline: false, password: false, text =>
        {
            string name = text.Trim();
            var editor = GetSharedPreferences(StampPreferences, FileCreationMode.Private)?.Edit();
            if (name.Length == 0) editor?.Remove(SignerKey);
            else editor?.PutString(SignerKey, name);
            editor?.Apply();
            if (name.Length == 0)
            {
                Toast.MakeText(this, "A stamp with a name and date needs your name.", ToastLength.Short)?.Show();
                return;
            }
            then?.Invoke();
        });
    }
}
