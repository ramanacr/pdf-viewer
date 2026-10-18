using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Forms;
using PdfEngine.Pages;
using PdfEngine.Redaction;
using PdfEngine.Rendering;
using PdfEngine.Save;
using PdfEngine.Signatures;
using PdfEngine.Text;
using PdfEngine.Vector;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Text;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The engines a test that is not about PDFium runs on: the vector engine's native services in
/// every build, and PDFium as well in the developer build (PdfiumRenderer=true), so a test of
/// viewer or engine-neutral behaviour covers the shipped engine and keeps the oracle's coverage.
/// Use with <c>[Theory, MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]</c>.
/// </summary>
internal static class TestEngines
{
    public const string Native = "native";
    public const string Pdfium = "pdfium";

    public static TheoryData<string> Names
    {
        get
        {
            var names = new TheoryData<string> { Native };
#if PDFIUM_RENDERER
            names.Add(Pdfium);
#endif
            return names;
        }
    }

    /// <summary>An <see cref="IPdfEngine"/> by name (see <see cref="Names"/>).</summary>
    public static IPdfEngine Create(string name) => name switch
    {
        Native => new NativeTestEngine(),
#if PDFIUM_RENDERER
        Pdfium => new PdfEngine.Pdfium.PdfiumEngine(),
#endif
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such engine in this build."),
    };

    /// <summary>
    /// The viewer's document service by engine name: the Auto-mode service the published app uses,
    /// or (developer build) the PDFium-mode service.
    /// </summary>
    public static IPdfDocumentService CreateService(string name, PdfSecurityPolicy? policy = null) => name switch
    {
        Native => new HybridVectorDocumentService(policy ?? PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto),
#if PDFIUM_RENDERER
        Pdfium => new PdfiumDocumentService(policy ?? PdfSecurityPolicy.DefaultStrict),
#endif
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such engine in this build."),
    };
}

/// <summary>
/// <see cref="IPdfEngine"/> over the vector engine, for tests: documents are opened as
/// <see cref="PdfNativeDocument"/> for the native document services, and text and pages are read
/// from the document's current revision (its <see cref="PdfNativeDocument.Vector"/>), the way the
/// viewer reads them in Auto mode.
/// </summary>
internal sealed class NativeTestEngine : IPdfEngine
{
    private readonly PdfNativeEngine _native;

    public NativeTestEngine()
    {
        Renderer = new NativeRenderer();
        TextService = new NativeText();
        _native = new PdfNativeEngine(Renderer);
    }

    public string EngineName => _native.EngineName;
    public string EngineVersion => typeof(PdfNativeEngine).Assembly.GetName().Version?.ToString() ?? "0";

    public async ValueTask<IPdfDocument> OpenDocumentAsync(string filePath, string? password = null, CancellationToken cancellationToken = default) =>
        await _native.OpenDocumentAsync(filePath, password, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IPdfDocument> OpenDocumentAsync(byte[] pdfBytes, string? password = null, CancellationToken cancellationToken = default) =>
        await _native.OpenDocumentAsync(pdfBytes, password, cancellationToken).ConfigureAwait(false);

    public IPdfRenderer Renderer { get; }
    public IPdfTextService TextService { get; }
    public IPdfAnnotationService AnnotationService => _native.AnnotationService;
    public IPdfSaveService SaveService => _native.SaveService;
    public IPdfFormService FormService => _native.FormService;
    public IPdfSignatureService SignatureService => _native.SignatureService;
    public IPdfPageOrganizerService PageOrganizer => _native.PageOrganizer;
    public IPdfRedactionService RedactionService => _native.RedactionService;

    public PdfNativeAttachmentService AttachmentService => _native.AttachmentService;
    public PdfNativeSafetyInspector SafetyInspector => _native.SafetyInspector;
    public PdfNativeSanitizer Sanitizer => _native.Sanitizer;

    public void Dispose() { }

    /// <summary>The document's current parse: a native document is read through its vector document.</summary>
    internal static IPdfDocument Current(IPdfDocument document) => document is PdfNativeDocument native ? native.Vector : document;

    private sealed class NativeRenderer : IPdfRenderer
    {
        public ValueTask<RenderedPage> RenderPageAsync(IPdfDocument document, RenderRequest request, CancellationToken cancellationToken = default) =>
            VectorPdfRenderer.Shared.RenderPageAsync(Current(document), request, cancellationToken);
    }

    private sealed class NativeText : IPdfTextService
    {
        private readonly PdfVectorTextService _text = new();

        public ValueTask<IReadOnlyList<TextSegment>> ExtractTextSegmentsAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default) =>
            _text.ExtractTextSegmentsAsync(Current(document), pageNumber, cancellationToken);

        public ValueTask<string> ExtractPageTextAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default) =>
            _text.ExtractPageTextAsync(Current(document), pageNumber, cancellationToken);

        public ValueTask<IReadOnlyList<PdfEngine.Text.SearchMatch>> SearchTextAsync(IPdfDocument document, string query, SearchOptions? options = null, CancellationToken cancellationToken = default) =>
            _text.SearchTextAsync(Current(document), query, options, cancellationToken);
    }
}

/// <summary>
/// Reading back what a test wrote, with the engine the published app has: the page text, the page
/// count, a rendered page and the annotations of a file or of in-memory bytes.
/// </summary>
internal static class NativeReadBack
{
    private static readonly PdfVectorTextService Text_ = new();

    public static async Task<string> TextAsync(byte[] pdf, int pageNumber = 1, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        return await Text_.ExtractPageTextAsync(doc, pageNumber);
    }

    public static async Task<string> TextAsync(string path, int pageNumber = 1, string? password = null) =>
        await TextAsync(await System.IO.File.ReadAllBytesAsync(path), pageNumber, password);

    public static async Task<int> PageCountAsync(string path, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(await System.IO.File.ReadAllBytesAsync(path), password: password);
        return doc.PageCount;
    }

    /// <summary>A page drawn by the vector renderer (BGRA, top-down), as the engine-neutral <see cref="RenderedPage"/>.</summary>
    public static async Task<RenderedPage> RenderAsync(byte[] pdf, RenderRequest request, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        return await VectorPdfRenderer.Shared.RenderPageAsync(doc, request);
    }
}
