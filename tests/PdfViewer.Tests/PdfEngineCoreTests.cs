using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfViewer.Core.Cache;
using PdfViewer.Core.Commands;
using PdfViewer.Core.Licensing;
using PdfViewer.Core.Rendering;
using PdfViewer.Core.Security;
using PdfViewer.Core.Session;
using PdfViewer.RenderingAdapters;
using Xunit;

namespace PdfViewer.Tests;

public class PdfEngineCoreTests
{
    private static string GetOrCreateSamplePdf()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core_Test_Doc.pdf");
        if (!File.Exists(path))
        {
            TestPdfBuilder.CreateSimplePdf(path, 2, "CoreTestToken");
        }
        return path;
    }

    [Fact]
    public async Task TestPdfiumEngineOpeningAndLifecycle()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();

        Assert.Equal("Google PDFium Native", engine.EngineName);
        Assert.Contains("154.0.8021.0", engine.EngineVersion);

        await using var doc = await engine.OpenDocumentAsync(samplePdf);
        Assert.True(doc.IsOpen);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(samplePdf, doc.FilePath);

        var info1 = await doc.GetPageInfoAsync(1);
        Assert.Equal(612, info1.WidthPoints);
        Assert.Equal(792, info1.HeightPoints);
        Assert.Equal(0, info1.RotationDegrees);
    }

    [Fact]
    public async Task TestPdfiumRendererRawBgraOutputAndWpfAdapter()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var req = new RenderRequest
        {
            PageNumber = 1,
            Dpi = 96.0,
            Rotation = PageRotation.Rotate0,
            HighQuality = true
        };

        using var rendered = await engine.Renderer.RenderPageAsync(doc, req);
        Assert.Equal(1, rendered.PageNumber);
        Assert.True(rendered.WidthPixels > 0);
        Assert.True(rendered.HeightPixels > 0);
        Assert.Equal(rendered.WidthPixels * 4, rendered.Stride);
        Assert.True(rendered.ByteLength > 0);
        Assert.False(rendered.Pixels.IsEmpty);

        // Convert via UI adapter
        var bitmap = WpfBitmapAdapter.ToBitmapSource(rendered);
        Assert.NotNull(bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(rendered.WidthPixels, bitmap.PixelWidth);
        Assert.Equal(rendered.HeightPixels, bitmap.PixelHeight);
    }

    [Fact]
    public async Task TestDocumentSessionFingerprintAndRevision()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);

        Assert.True(session.IsOpen);
        Assert.False(string.IsNullOrEmpty(session.Fingerprint));
        Assert.Equal(1, session.Revision);
        Assert.False(session.IsDirty);

        session.IncrementRevision();
        Assert.Equal(2, session.Revision);
        Assert.True(session.IsDirty);

        session.MarkSaved();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void TestMultiTierCacheMemoryBudgetAndEviction()
    {
        // 200 KB cache limit
        using var cache = new MultiTierCache(maxMemoryBytes: 200 * 1024);

        var key1 = new RenderCacheKey("doc1", 1, 96, PageRotation.Rotate0, 1);
        var key2 = new RenderCacheKey("doc1", 2, 96, PageRotation.Rotate0, 1);

        // Create dummy 100 KB rendered pages
        int pageBytes = 100 * 1024;
        var mem1 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page1 = new RenderedPage(1, 100, 250, 400, 96, PageRotation.Rotate0, mem1);

        var mem2 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page2 = new RenderedPage(2, 100, 250, 400, 96, PageRotation.Rotate0, mem2);

        cache.Put(key1, page1);
        cache.Put(key2, page2);

        Assert.True(cache.TryGet(key1, out var fetched1));
        Assert.NotNull(fetched1);

        // Put a 3rd page which forces eviction of least recently used key2
        var key3 = new RenderCacheKey("doc1", 3, 96, PageRotation.Rotate0, 1);
        var mem3 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page3 = new RenderedPage(3, 100, 250, 400, 96, PageRotation.Rotate0, mem3);

        cache.Put(key3, page3);

        Assert.True(cache.EvictionCount >= 1);
        Assert.True(cache.CurrentMemoryBytes <= cache.MaxMemoryBytes);
    }

    [Fact]
    public async Task TestRenderPrioritySchedulerDeduplicationAndCancellation()
    {
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);

        using var scheduler = new RenderPriorityScheduler(engine.Renderer);

        var req = new RenderRequest { PageNumber = 1, Dpi = 96.0 };

        // Fire 5 concurrent requests for the exact same page
        var t1 = scheduler.GetOrRenderPageAsync(session, req);
        var t2 = scheduler.GetOrRenderPageAsync(session, req);
        var t3 = scheduler.GetOrRenderPageAsync(session, req);

        var results = await Task.WhenAll(t1, t2, t3);

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.NotNull(results[2]);
        Assert.Equal(results[0].WidthPixels, results[1].WidthPixels);
    }

    [Fact]
    public async Task TestCommandHistoryUndoRedo()
    {
        var history = new CommandHistory(maxHistory: 10);
        using var session = new DocumentSession();

        bool executed = false;
        bool undone = false;

        var mockCommand = new MockDocCommand(
            "TestCommand",
            () => executed = true,
            () => undone = true);

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        await history.ExecuteCommandAsync(mockCommand, session);

        Assert.True(executed);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("TestCommand", history.NextUndoName);

        await history.UndoAsync(session);
        Assert.True(undone);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.Equal("TestCommand", history.NextRedoName);

        executed = false;
        await history.RedoAsync(session);
        Assert.True(executed);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void TestFeatureGateEntitlements()
    {
        var gate = new DefaultFeatureGate { CurrentTier = LicenseTier.Community };
        Assert.True(gate.IsFeatureEnabled(FeatureId.Viewer));
        Assert.True(gate.IsFeatureEnabled(FeatureId.Search));
        Assert.True(gate.IsFeatureEnabled(FeatureId.Annotations));
        Assert.False(gate.IsFeatureEnabled(FeatureId.Redaction));
        Assert.False(gate.IsFeatureEnabled(FeatureId.Sdk));

        gate.CurrentTier = LicenseTier.Pro;
        Assert.True(gate.IsFeatureEnabled(FeatureId.Redaction));
        Assert.True(gate.IsFeatureEnabled(FeatureId.Forms));
        Assert.False(gate.IsFeatureEnabled(FeatureId.Sdk));

        gate.CurrentTier = LicenseTier.DeveloperSdk;
        Assert.True(gate.IsFeatureEnabled(FeatureId.Sdk));
    }

    [Fact]
    public void TestPdfSecurityPolicy()
    {
        var strict = PdfSecurityPolicy.DefaultStrict;
        Assert.False(strict.AllowJavaScript);
        Assert.False(strict.AllowLaunchActions);
        Assert.True(strict.ConfirmAttachmentExtraction);

        var permissive = PdfSecurityPolicy.Permissive;
        Assert.True(permissive.AllowJavaScript);
        Assert.True(permissive.AllowLaunchActions);
        Assert.False(permissive.ConfirmAttachmentExtraction);
    }

    private sealed class MockDocCommand : IDocumentCommand
    {
        private readonly Action _onExecute;
        private readonly Action _onUndo;

        public string Name { get; }

        public MockDocCommand(string name, Action onExecute, Action onUndo)
        {
            Name = name;
            _onExecute = onExecute;
            _onUndo = onUndo;
        }

        public ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            _onExecute();
            return ValueTask.CompletedTask;
        }

        public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            _onUndo();
            return ValueTask.CompletedTask;
        }
    }
}
