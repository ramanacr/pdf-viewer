using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Custom stamps: the stamp menu's "Custom stamp…" makes one from a PNG or JPEG picture or a page
/// of a PDF the reader picks (read in the browser, never uploaded; a PDF's page chosen with a
/// preview), and the reader clicks or drags to place it (<see cref="ViewerEditSession.AddCustomStamp"/>).
/// A made stamp may be kept in this browser's stamp library (IndexedDB, capped; viewer.js) and
/// used again or deleted. An embedded viewer keeps nothing in the browser: its origin is shared by
/// every host page. The embed SDK's addCustomStamp comes here too.
/// </summary>
public sealed partial class Viewer
{
    /// <summary>A stamp kept in this browser's library.</summary>
    private sealed record SavedStamp(string Id, string Name, string Kind, long Size);

    private const string CustomStampOption = "__custom";
    private const string CurrentStampOption = "__current";

    /// <summary>The custom stamp the stamp tool places (<see cref="Tool.CustomStamp"/>), and its name in the menu.</summary>
    private ViewerCustomStamp? _customStamp;
    private string _customStampName = "";

    private List<SavedStamp> _savedStamps = [];
    private bool _stampLibrary;   // this browser keeps stamps (IndexedDB works, not embedded)

    // The dialog: the file picked, the PDF it opened (a picture's stamp, or the PDF itself) and the page shown.
    private byte[]? _pickedBytes;
    private string _pickedName = "";
    private ViewerCustomStampKind? _pickedKind;
    private ViewerCustomStamp? _pickedStamp;
    private ViewerDocument? _pickedPdf;
    private int _pickedPage = 1;
    private bool _previewDirty;
    private bool _saveStamp = true;
    private bool _stampBusy;
    private string? _stampProblem;

    /// <summary>The stamp menu's value: a standard stamp's name, "C:id" for a saved stamp, or the custom stamp in use.</summary>
    private string StampValue =>
        _tool != Tool.CustomStamp || _customStamp == null ? _stampName
        : _savedStamps.Any(s => s.Id == _customStamp.Hash) ? "C:" + _customStamp.Hash
        : CurrentStampOption;

    private bool CanKeepStamps => !Embedded;

    private async Task PickStampAsync(ChangeEventArgs e)
    {
        string value = e.Value?.ToString() ?? "";
        if (value == CustomStampOption)
        {
            await OpenCustomStampDialogAsync();
        }
        else if (value.StartsWith("C:", StringComparison.Ordinal))
        {
            await UseSavedStampAsync(value[2..]);
        }
        else if (value != CurrentStampOption)
        {
            _stampName = value;
            _customStamp = null;
            if (_tool != Tool.Stamp) SetTool(Tool.Stamp);
        }
        StateHasChanged();
    }

    /// <summary>Places <paramref name="stamp"/> with the next click or drag.</summary>
    private void UseCustomStamp(ViewerCustomStamp stamp, string name)
    {
        _customStamp = stamp;
        _customStampName = name;
        if (_tool != Tool.CustomStamp) SetTool(Tool.CustomStamp);
        _status = $"Click where the stamp \"{name}\" goes, or drag its box.";
    }

    // ---------------------------------------------------------------- the library

    private async Task RefreshSavedStampsAsync()
    {
        if (!CanKeepStamps) return;
        try
        {
            string json = await CanvasInterop.StampLibraryList();
            // Read by hand (no reflection: the published app is trimmed).
            using var doc = JsonDocument.Parse(json);
            _stampLibrary = doc.RootElement.ValueKind == JsonValueKind.Array;
            var list = new List<SavedStamp>();
            if (_stampLibrary)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object
                        || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: 64 } sid
                        || !e.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                    string kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "Page" ? "Page" : "Image";
                    long size = e.TryGetProperty("size", out var z) && z.TryGetInt64(out long n) ? n : 0;
                    list.Add(new SavedStamp(sid, Shorten(name.GetString()!), kind, size));
                }
            }
            _savedStamps = list;
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            _stampLibrary = false;
            _savedStamps = [];
        }
    }

    private async Task RefreshSavedStampsThenRenderAsync()
    {
        await RefreshSavedStampsAsync();
        StateHasChanged();
    }

    private static string Shorten(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "Stamp";
        return name.Length > 60 ? name[..60] : name;
    }

    private async Task UseSavedStampAsync(string id)
    {
        _stampProblem = null;
        try
        {
            // The bytes come back through OnStampLibraryBytes.
            if (!await CanvasInterop.StampLibraryLoad(id)) _status = "That stamp is no longer in this browser's library.";
        }
        catch (JSException)
        {
            _status = "This browser's stamp library cannot be read.";
        }
        await RefreshSavedStampsAsync();
    }

    [JSInvokable]
    public async Task OnStampLibraryBytes(string id, string kind, string name, byte[] pdf)
    {
        try
        {
            // Checked again: what the browser kept is not trusted more than a new file.
            var stamp = ViewerCustomStamp.FromKept(kind == "Page" ? ViewerCustomStampKind.Page : ViewerCustomStampKind.Image, pdf);
            if (stamp.Hash != id) throw new ViewerAnnotationException("The saved stamp is damaged; delete it and make it again.");
            if (_dialog == Dialog.CustomStamp) await CloseCustomStampDialogAsync();
            UseCustomStamp(stamp, Shorten(name));
        }
        catch (ViewerAnnotationException ex)
        {
            _status = ex.Message;
        }
        StateHasChanged();
    }

    private async Task DeleteSavedStampAsync(SavedStamp stamp)
    {
        try
        {
            if (!await CanvasInterop.StampLibraryDelete(stamp.Id)) _stampProblem = "This browser did not let the viewer delete the stamp.";
        }
        catch (JSException)
        {
            _stampProblem = "This browser did not let the viewer delete the stamp.";
        }
        await RefreshSavedStampsAsync();
        // Stamps already placed stay in their documents.
        if (_customStamp?.Hash == stamp.Id && _tool == Tool.CustomStamp) _status = $"\"{stamp.Name}\" is no longer saved; it can still be placed until you choose another stamp.";
    }

    // ---------------------------------------------------------------- the dialog

    private async Task OpenCustomStampDialogAsync()
    {
        await ClearPickedAsync();
        _stampProblem = null;
        _dialog = Dialog.CustomStamp;
        _focusAfterRender = "stamp-pick";
        await RefreshSavedStampsAsync();
    }

    private async Task CloseCustomStampDialogAsync()
    {
        _dialog = Dialog.None;
        await ClearPickedAsync();
    }

    private async Task ClearPickedAsync()
    {
        _pickedBytes = null;
        _pickedKind = null;
        _pickedStamp = null;
        _pickedName = "";
        _pickedPage = 1;
        if (_pickedPdf is { } pdf)
        {
            _pickedPdf = null;
            await pdf.DisposeAsync();
        }
    }

    private void PickStampFile() => CanvasInterop.PickStampFile();

    [JSInvokable]
    public void OnStampFileRejected(string message)
    {
        _stampProblem = message;
        StateHasChanged();
    }

    /// <summary>The reader picked a file for a custom stamp: a picture is made into one now (and checked), a PDF opened to choose its page.</summary>
    [JSInvokable]
    public async Task OnStampFile(string name, byte[] bytes)
    {
        if (_dialog != Dialog.CustomStamp) return;
        await ClearPickedAsync();
        _stampProblem = null;
        _stampBusy = true;
        StateHasChanged();
        try
        {
            var kind = ViewerCustomStamp.Sniff(bytes) ?? throw new ViewerAnnotationException("Choose a PNG or JPEG picture or a PDF.");
            _pickedName = Shorten(Path.GetFileNameWithoutExtension(name));
            if (kind == ViewerCustomStampKind.Image)
            {
                _pickedStamp = ViewerCustomStamp.FromImage(bytes);
                _pickedPdf = await ViewerDocument.OpenAsync(_pickedStamp.Pdf);
            }
            else
            {
                if (bytes.Length > ViewerCustomStamp.MaxPdfBytes) throw new ViewerAnnotationException($"The PDF is larger than {ViewerCustomStamp.MaxPdfBytes / (1024 * 1024)} MB.");
                try
                {
                    // No password is asked for: an encrypted PDF is refused (open it in the viewer first).
                    _pickedPdf = await ViewerDocument.OpenAsync(bytes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    throw new ViewerAnnotationException("This PDF cannot be read, or it is encrypted. Use an unencrypted copy.");
                }
                if (_pickedPdf.IsEncrypted) throw new ViewerAnnotationException("This PDF is encrypted. Use an unencrypted copy.");
                _pickedBytes = bytes;
            }
            _pickedKind = kind;
            _previewDirty = true;
        }
        catch (ViewerAnnotationException ex)
        {
            await ClearPickedAsync();
            _stampProblem = ex.Message;
        }
        finally
        {
            _stampBusy = false;
        }
        StateHasChanged();
    }

    private void StepPickedPage(int delta) => SetPickedPage(_pickedPage + delta);

    private void SetPickedPage(int page)
    {
        if (_pickedPdf == null) return;
        int p = Math.Clamp(page, 1, _pickedPdf.PageCount);
        if (p == _pickedPage) return;
        _pickedPage = p;
        _previewDirty = true;
    }

    private void OnPickedPageInput(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int page)) SetPickedPage(page);
    }

    /// <summary>After a render: the dialog's preview of the picture or the chosen page, at most 280 by 200 CSS pixels.</summary>
    private async Task DrawStampPreviewAsync()
    {
        if (!_previewDirty || _dialog != Dialog.CustomStamp || _pickedPdf is not { } pdf) return;
        _previewDirty = false;
        int page = _pickedPage;
        var size = pdf.Pages[page - 1];
        double scale = Math.Min(280 / size.Width, 200 / size.Height) * CanvasInterop.DevicePixelRatio();
        try
        {
            var bitmap = await pdf.RenderPageAsync(page, scale);
            if (_pickedPdf == pdf && _pickedPage == page) CanvasInterop.DrawStampPreview(bitmap.Pixels, bitmap.Width, bitmap.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException and not OutOfMemoryException)
        {
            // The preview stays blank; making the stamp says what is wrong.
        }
    }

    /// <summary>The dialog's "Place stamp": the stamp is made (and kept in the library when asked), then placed with a click or drag.</summary>
    private async Task UsePickedStampAsync()
    {
        if (_pickedKind is not { } kind || _stampBusy) return;
        _stampBusy = true;
        _stampProblem = null;
        try
        {
            var stamp = kind == ViewerCustomStampKind.Image ? _pickedStamp! : ViewerCustomStamp.FromPdf(_pickedBytes!, _pickedPage);
            string name = kind == ViewerCustomStampKind.Page && _pickedPdf?.PageCount > 1 ? Shorten($"{_pickedName}, page {_pickedPage}") : _pickedName;
            string? notKept = null;
            if (_saveStamp && CanKeepStamps)
            {
                try
                {
                    notKept = await CanvasInterop.StampLibrarySave(stamp.Hash, name, stamp.Kind.ToString(), stamp.Pdf);
                }
                catch (JSException)
                {
                    notKept = "This browser does not let the viewer keep stamps.";
                }
                await RefreshSavedStampsAsync();
            }
            await CloseCustomStampDialogAsync();
            UseCustomStamp(stamp, name);
            if (!string.IsNullOrEmpty(notKept)) _status = $"{notKept} The stamp can still be placed now.";
        }
        catch (ViewerAnnotationException ex)
        {
            _stampProblem = ex.Message;
        }
        finally
        {
            _stampBusy = false;
        }
    }

    // ---------------------------------------------------------------- the embed SDK

    /// <summary>
    /// addCustomStamp: a stamp from the picture or PDF the host page sent, fitted in its box (PDF
    /// user space) or centred on the page at its usual size. Refused as the session refuses.
    /// </summary>
    private ViewerAnnotation AddCustomStampFromHost(ViewerEditSession session, EmbedCall call)
    {
        var o = call.CustomStamp ?? throw new ViewerAnnotationException("addCustomStamp: give { data, pageNumber }.");
        byte[] bytes = call.Bytes is { Length: > 0 } b ? b : throw new ViewerAnnotationException("addCustomStamp: data must be the bytes of a PNG or JPEG picture or a PDF.");
        if (o.Page > session.Document.Geometry.Count)
            throw new ViewerAnnotationException($"addCustomStamp: the document has {session.Document.Geometry.Count} pages.");
        var stamp = ViewerCustomStamp.Sniff(bytes) switch
        {
            ViewerCustomStampKind.Image => ViewerCustomStamp.FromImage(bytes),
            ViewerCustomStampKind.Page => ViewerCustomStamp.FromPdf(bytes, o.SourcePage),
            _ => throw new ViewerAnnotationException("addCustomStamp: data must be a PNG or JPEG picture or a PDF."),
        };
        var g = session.Document.Geometry[o.Page - 1];
        var box = o.Box is { } user ? g.ToShown(user) : new ViewerRect(g.ShownWidth / 2, g.ShownHeight / 2, 0, 0);
        return session.AddCustomStamp(o.Page, box, stamp);
    }
}
