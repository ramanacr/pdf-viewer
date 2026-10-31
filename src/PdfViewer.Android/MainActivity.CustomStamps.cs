using Android.App;
using Android.Content;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Custom stamps: "Custom stamp" in the stamp list asks the system picker (the Storage Access
/// Framework, ACTION_OPEN_DOCUMENT: no storage permission) for a PNG or JPEG picture or a PDF; a
/// PDF's page is chosen by number. The shared session makes the stamp
/// (<see cref="ViewerCustomStamp"/>, with its limits on untrusted files) and places it with a tap
/// (<see cref="ViewerEditSession.AddCustomStamp"/>). A made stamp may be kept in this app's
/// private files (<see cref="ViewerStampLibrary"/>; backups are off), chosen again from the stamp
/// list, or deleted there. Nothing is sent anywhere.
/// </summary>
public sealed partial class MainActivity
{
    private const int CustomStampRequest = 5;

    /// <summary>The custom stamp the stamp tool places (instead of <see cref="_stamp"/>), and its name.</summary>
    private ViewerCustomStamp? _customStamp;
    private string _customStampName = "";

    private ViewerStampLibrary StampLibrary => new(System.IO.Path.Combine(FilesDir!.AbsolutePath, "stamps"));

    private sealed class StampFileTooLargeException(string message) : IOException(message);

    /// <summary>Asks the system picker for a picture or a PDF to make a stamp from.</summary>
    private void PickCustomStampFile()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "image/png", "image/jpeg", "application/pdf" });
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivityForResult(intent, CustomStampRequest);
    }

    /// <summary>The file, read only up to the largest a stamp is made from.</summary>
    private byte[] ReadStampFile(Uri uri)
    {
        using var input = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("No stream for the file.");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > ViewerCustomStamp.MaxPdfBytes)
                throw new StampFileTooLargeException($"This file is larger than {ViewerCustomStamp.MaxPdfBytes / (1024 * 1024)} MB, the most a stamp is made from.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>The picker gave a file: a picture is made into a stamp, a PDF's page is asked for first.</summary>
    private async Task MakeCustomStampAsync(Uri uri)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(DisplayName(uri) ?? "") is { Length: > 0 } n ? n : "Stamp";
        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => ReadStampFile(uri));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            Tell(ex is StampFileTooLargeException ? ex.Message : "The file could not be read.");
            return;
        }
        switch (ViewerCustomStamp.Sniff(bytes))
        {
            case ViewerCustomStampKind.Image:
                await OfferCustomStampAsync(() => ViewerCustomStamp.FromImage(bytes), name);
                break;
            case ViewerCustomStampKind.Page:
            {
                int pages;
                try
                {
                    pages = await Task.Run(() => ViewerCustomStamp.PageCount(bytes));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Tell(Problem(ex));
                    return;
                }
                if (pages == 1)
                {
                    await OfferCustomStampAsync(() => ViewerCustomStamp.FromPdf(bytes, 1), name);
                    break;
                }
                AskText($"Which page of the PDF? (1 to {pages})", "1", 6, multiline: false, password: false, text =>
                {
                    if (!int.TryParse(text.Trim(), out int page) || page < 1 || page > pages)
                    {
                        Tell($"Choose a page from 1 to {pages}.");
                        return;
                    }
                    _ = OfferCustomStampAsync(() => ViewerCustomStamp.FromPdf(bytes, page), $"{name}, page {page}");
                });
                break;
            }
            default:
                Tell("Choose a PNG or JPEG picture or a PDF.");
                break;
        }
    }

    /// <summary>Makes the stamp (on a worker thread), then offers to keep it, and places it with the next tap.</summary>
    private async Task OfferCustomStampAsync(Func<ViewerCustomStamp> make, string name)
    {
        ViewerCustomStamp stamp;
        try
        {
            stamp = await Task.Run(make);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tell(Problem(ex));
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Custom stamp")!
            .SetMessage($"Keep \"{name}\" in this app's stamp library, to use it again? It stays on this device.")!
            .SetPositiveButton("Keep and place", async (_, _) =>
            {
                var library = StampLibrary;
                string? notKept;
                try
                {
                    notKept = await Task.Run(() => library.Save(stamp, name));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notKept = "The stamp could not be kept on this device.";
                }
                if (notKept != null) Tell(notKept);
                ArmCustomStamp(stamp, name);
            })!
            .SetNegativeButton("Place only", (_, _) => ArmCustomStamp(stamp, name))!
            .Show();
    }

    /// <summary>The stamp tool is on with a custom stamp: the next tap on a page places it.</summary>
    private void ArmCustomStamp(ViewerCustomStamp stamp, string name)
    {
        _stamp = null;
        _customStamp = stamp;
        _customStampName = name;
        if (_pages.Tool != PageView.PageTool.Stamp) SetTool(PageView.PageTool.Stamp);
        Toast.MakeText(this, $"Tap where the stamp \"{name}\" goes.", ToastLength.Short)?.Show();
    }

    /// <summary>A saved stamp, read back (and checked) on a worker thread, then placed with the next tap.</summary>
    private async Task UseSavedStampAsync(ViewerSavedStamp saved)
    {
        var library = StampLibrary;
        ViewerCustomStamp? stamp;
        try
        {
            stamp = await Task.Run(() => library.Load(saved.Id));
        }
        catch (OutOfMemoryException)
        {
            stamp = null;
        }
        if (stamp == null)
        {
            Tell("That saved stamp cannot be read. Delete it and make it again.");
            return;
        }
        ArmCustomStamp(stamp, saved.Name);
    }

    /// <summary>The saved stamps, to delete one (stamps already placed stay in their documents).</summary>
    private void DeleteSavedStamp(IReadOnlyList<ViewerSavedStamp> saved)
    {
        new AlertDialog.Builder(this)
            .SetTitle("Delete a saved stamp")!
            .SetItems(saved.Select(s => s.Name).ToArray(), (_, e) =>
            {
                var chosen = saved[e.Which];
                new AlertDialog.Builder(this)
                    .SetMessage($"Delete \"{chosen.Name}\" from this app's stamp library? Stamps already placed stay in their documents.")!
                    .SetPositiveButton("Delete", (_, _) =>
                    {
                        if (!StampLibrary.Delete(chosen.Id)) Tell("The stamp could not be deleted.");
                    })!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show();
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>What the reader is told when a stamp cannot be made: the reason, never the file's content.</summary>
    private static string Problem(Exception ex) => ex switch
    {
        ViewerAnnotationException a => a.Message,
        OutOfMemoryException => "This file is too large to make a stamp on this device. Choose a smaller one.",
        _ => $"The stamp could not be made ({ex.GetType().Name}).",
    };

    private void Tell(string message) =>
        Toast.MakeText(this, message, message.Length > 60 ? ToastLength.Long : ToastLength.Short)?.Show();
}
