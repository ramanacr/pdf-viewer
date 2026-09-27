using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfViewer.ViewModels;

namespace PdfViewer.Services;

/// <summary>
/// Filling the open document's form: the fields of the current revision, and each commit applied
/// as an incremental update that the document services reload. Every commit is a new revision in
/// memory; nothing touches the file until the document is saved.
/// </summary>
public sealed class FormSession : IDisposable
{
    private readonly IPdfDocumentService _service;
    private PdfVectorDocument _document;
    private byte[] _bytes;
    private readonly string? _password;

    public PdfAcroForm Form { get; private set; }
    /// <summary>Why fields are shown but cannot be changed (null when they can).</summary>
    public string? ReadOnlyReason { get; }
    public int Revisions { get; private set; }

    private FormSession(IPdfDocumentService service, PdfVectorDocument document, byte[] bytes, PdfAcroForm form, string? readOnlyReason, string? password)
    {
        _service = service;
        _password = password;
        _document = document;
        _bytes = bytes;
        Form = form;
        ReadOnlyReason = readOnlyReason;
    }

    /// <summary>The session, or null when the document has no fillable fields.</summary>
    public static async Task<FormSession?> OpenAsync(IPdfDocumentService service, string? password, CancellationToken ct = default)
    {
        if (service.CurrentBytes is not { } bytes)
            return null;
        PdfVectorDocument doc;
        try
        {
            doc = await PdfVectorDocument.OpenAsync(bytes, service.CurrentFilePath, cancellationToken: ct, password: password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        var form = PdfAcroForm.Read(doc);
        if (form == null || !form.Fields.Any(Presented))
        {
            doc.Dispose();
            return null;
        }
        string? reason = doc.WasRepaired ? "This file's structure is damaged; its form cannot be filled until it is repaired."
                       : service.IsDecryptedCopy ? "This document is encrypted for specific recipients; its form cannot be filled here."
                       : null;
        return new FormSession(service, doc, bytes, form, reason, password);
    }

    /// <summary>Fields the page layer shows: everything fillable, and signature fields still waiting to be signed.</summary>
    private static bool Presented(PdfFormField f) =>
        f.Kind != PdfFormFieldKind.PushButton && !(f.Kind == PdfFormFieldKind.Signature && f.IsSigned);

    /// <summary>View models for every visible widget, in tab order (page, then position in the page's /Annots).</summary>
    public List<FormFieldViewModel> CreateFieldViewModels()
    {
        var list = new List<FormFieldViewModel>();
        int tab = 0;
        foreach (var field in Form.Fields.OrderBy(f => f.Widgets.Select(w => w.PageNumber).DefaultIfEmpty(int.MaxValue).Min()))
        {
            if (!Presented(field))
                continue;
            var created = FormFieldViewModel.FromField(field, PageBox, tab).ToList();
            tab += created.Count;
            list.AddRange(created);
        }
        return list.OrderBy(f => f.PageNumber).ThenBy(f => f.Bounds.Y).ThenBy(f => f.Bounds.X).ToList();
    }

    private (double X, double Y, double W, double H)? PageBox(int page)
    {
        if (page < 1 || page > _document.PageCount) return null;
        var crop = _document.PageTree.Pages[page - 1].CropBox;
        return (crop.X, crop.Y, crop.Width, crop.Height);
    }

    /// <summary>
    /// Applies changes as a new revision and reloads the document services with it. The field
    /// scripts' rules apply: a refused value throws <see cref="PdfFieldValidationException"/>,
    /// values are stored and shown as their formats say, and calculated fields are recalculated
    /// in the same revision. Returns the names of every field that changed.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyList<PdfFieldChange> changes, CancellationToken ct = default)
    {
        if (ReadOnlyReason != null)
            throw new InvalidOperationException(ReadOnlyReason);
        if (changes.Count == 0)
            return Array.Empty<string>();
        var prepared = PdfFormScripts.Prepare(Form, changes);
        byte[] next = PdfFormFiller.Apply(_document, _bytes, prepared);
        await _service.ReloadFromBytesAsync(next, ct);
        var doc = await PdfVectorDocument.OpenAsync(next, _service.CurrentFilePath, cancellationToken: ct, password: _password);
        var form = PdfAcroForm.Read(doc) ?? throw new InvalidOperationException("The filled document lost its form.");
        _document.Dispose();
        _document = doc;
        _bytes = next;
        Form = form;
        Revisions++;
        return prepared.Select(c => c.FullName).Distinct().ToList();
    }

    public void Dispose() => _document.Dispose();
}
