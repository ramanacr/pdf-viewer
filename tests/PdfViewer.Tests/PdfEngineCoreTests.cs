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
    public async Task TestRedactionActuallyRemovesUnderlyingText()
    {
        // SECURITY regression test: redaction previously only drew a black box and flattened
        // it, leaving the original text objects intact underneath. The "redacted" output
        // gave the text straight back to any extraction or copy-paste - the classic
        // real-world redaction disclosure.
        string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redaction_Source.pdf");
        TestPdfBuilder.CreateSimplePdf(source, 1, "TopSecretToken");

        string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Redaction_Output.pdf");
        if (File.Exists(target)) File.Delete(target);

        using IPdfEngine engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            // Sanity check: the secret really is extractable from the source.
            string before = await engine.TextService.ExtractPageTextAsync(doc, 1);
            Assert.Contains("TopSecretToken", before);

            // Cover the upper portion of the page, where the sample text is drawn.
            var redaction = new PdfEngine.Redaction.RedactionArea
            {
                PageNumber = 1,
                Bounds = new PdfRect(0.0, 0.0, 1.0, 0.4)
            };

            await engine.RedactionService.ApplyRedactionsAsync(doc, target, new[] { redaction });
        }

        Assert.True(File.Exists(target));

        await using var redacted = await engine.OpenDocumentAsync(target);
        string after = await engine.TextService.ExtractPageTextAsync(redacted, 1);

        Assert.DoesNotContain("TopSecretToken", after);
    }

    [Fact]
    public async Task TestDefaultSaveProducesAReadableDocument()
    {
        // Regression test: SaveOptions defaulted to Incremental, and FPDF_SaveAsCopy with
        // FPDF_INCREMENTAL emits ONLY the incremental update section. Written into a
        // brand-new file that has no base document, the result was a PDF no reader could
        // open. RemoveUnusedObjects also defaulted true and was mapped to
        // FPDF_REMOVE_SECURITY, silently stripping encryption.
        string source = GetOrCreateSamplePdf();
        string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DefaultSave_Output.pdf");
        if (File.Exists(target)) File.Delete(target);

        using IPdfEngine engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(source))
        {
            // Default options - exactly what a caller gets from SaveAsync(doc, path).
            await engine.SaveService.SaveAsync(doc, target);
        }

        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 0);

        // The saved file must be a complete, re-openable document.
        await using var reopened = await engine.OpenDocumentAsync(target);
        Assert.True(reopened.IsOpen);
        Assert.Equal(2, reopened.PageCount);

        string text = await engine.TextService.ExtractPageTextAsync(reopened, 1);
        Assert.Contains("CoreTestToken", text);
    }

    [Fact]
    public async Task TestIntrinsicPageRotationIsNotAppliedTwice()
    {
        // Regression test: the renderer added the page's own /Rotate to the requested
        // rotation, but FPDF_RenderPageBitmap already applies /Rotate internally and
        // FPDF_GetPageWidthF/HeightF already return rotation-adjusted sizes. A /Rotate 90
        // page therefore rendered 90 degrees off, into a transposed bitmap.
        string rotatedPdf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rotated90_Test_Doc.pdf");
        TestPdfBuilder.CreateRotatedPdf(rotatedPdf, 90);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(rotatedPdf);

        // The page is 612x792 with /Rotate 90, so its DISPLAY size is 792x612 (landscape).
        var info = await doc.GetPageInfoAsync(1);
        Assert.Equal(792, info.WidthPoints);
        Assert.Equal(612, info.HeightPoints);

        // Rendering with no additional user rotation must preserve that landscape shape.
        var rendered = await engine.Renderer.RenderPageAsync(doc,
            new RenderRequest { PageNumber = 1, Dpi = 72.0, Rotation = PageRotation.Rotate0 });
        using (rendered)
        {
            Assert.True(rendered.WidthPixels > rendered.HeightPixels,
                $"Expected landscape output for a /Rotate 90 page, got {rendered.WidthPixels}x{rendered.HeightPixels}.");
        }

        // Adding a further 90 degrees must flip it back to portrait - exactly once.
        var rotatedAgain = await engine.Renderer.RenderPageAsync(doc,
            new RenderRequest { PageNumber = 1, Dpi = 72.0, Rotation = PageRotation.Rotate90 });
        using (rotatedAgain)
        {
            Assert.True(rotatedAgain.HeightPixels > rotatedAgain.WidthPixels,
                $"Expected portrait output after a further 90 degrees, got {rotatedAgain.WidthPixels}x{rotatedAgain.HeightPixels}.");
        }
    }

    [Fact]
    public async Task TestRenderReportsEffectiveDpiWhenSizedByPixels()
    {
        // Regression test: RenderedPage.Dpi echoed request.Dpi even when the raster size was
        // driven by TargetWidth/HeightPixels, so consumers using it for physical sizing
        // (BitmapSource.Create) rendered at the wrong scale.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        // 612pt wide page rasterized to 1224px => 144 DPI, regardless of the requested 96.
        using var rendered = await engine.Renderer.RenderPageAsync(doc, new RenderRequest
        {
            PageNumber = 1,
            Dpi = 96.0,
            TargetWidthPixels = 1224,
            TargetHeightPixels = 1584
        });

        Assert.Equal(144.0, rendered.Dpi, precision: 1);
    }

    [Fact]
    public async Task TestOversizedRenderIsRejectedInsteadOfOverflowing()
    {
        // Regression test: stride/buffer size were computed in int, so a large page at a high
        // DPI overflowed - either throwing far from the cause, or wrapping to a small
        // positive size and handing PDFium a buffer smaller than the bitmap it would write.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(samplePdf);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var _ = await engine.Renderer.RenderPageAsync(doc, new RenderRequest
            {
                PageNumber = 1,
                TargetWidthPixels = 40000,
                TargetHeightPixels = 40000
            });
        });

        Assert.Contains("exceed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestNativeMemoryOwnerThrowsAfterDispose()
    {
        // Regression test: the memory manager kept its raw pointer after Dispose freed the
        // block, so reads silently returned a Span over freed heap instead of throwing.
        var owner = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(1024);
        Assert.Equal(1024, owner.Memory.Length);

        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory.Span[0]);
    }

    [Fact]
    public async Task TestAttachDocumentDisposesPreviousDocument()
    {
        // Regression test: attaching a second document over the first silently dropped it,
        // leaking its PDFium handle and backing buffer and keeping the file locked.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();

        var first = await engine.OpenDocumentAsync(samplePdf);
        var second = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(first);
        Assert.True(first.IsOpen);

        session.AttachDocument(second);

        Assert.False(first.IsOpen);      // previous document was closed, not leaked
        Assert.True(second.IsOpen);
    }

    [Fact]
    public async Task TestSessionTokenIsCancelledOnClose()
    {
        // Regression test: Close() disposed the document with no signal to in-flight work,
        // so a queued render could hand a freed PDFium handle back to the native library.
        string samplePdf = GetOrCreateSamplePdf();
        using IPdfEngine engine = new PdfiumEngine();
        var doc = await engine.OpenDocumentAsync(samplePdf);

        using var session = new DocumentSession();
        session.AttachDocument(doc);
        Assert.False(session.SessionToken.IsCancellationRequested);

        session.Close();
        Assert.True(session.SessionToken.IsCancellationRequested);
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

        cache.Put(key1, page1).Dispose();
        cache.Put(key2, page2).Dispose();

        Assert.True(cache.TryGet(key1, out var fetched1));
        Assert.NotNull(fetched1);
        using (fetched1)
        {
            Assert.Equal(1, fetched1!.Page.PageNumber);
        }

        // Put a 3rd page which forces eviction of least recently used key2
        var key3 = new RenderCacheKey("doc1", 3, 96, PageRotation.Rotate0, 1);
        var mem3 = new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes);
        var page3 = new RenderedPage(3, 100, 250, 400, 96, PageRotation.Rotate0, mem3);

        cache.Put(key3, page3).Dispose();

        Assert.True(cache.EvictionCount >= 1);
        Assert.True(cache.CurrentMemoryBytes <= cache.MaxMemoryBytes);
    }

    [Fact]
    public void TestCacheDoesNotFreePageStillHeldByCaller()
    {
        // Regression test: TryGet used to return the raw RenderedPage while the cache kept
        // ownership, so an eviction disposed it under the caller and reads of the pixel
        // span hit freed unmanaged memory.
        using var cache = new MultiTierCache(maxMemoryBytes: 200 * 1024);

        int pageBytes = 100 * 1024;
        var keyA = new RenderCacheKey("doc1", 1, 96, PageRotation.Rotate0, 1);
        var pageA = new RenderedPage(1, 100, 250, 400, 96, PageRotation.Rotate0,
            new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(pageBytes));

        cache.Put(keyA, pageA).Dispose();

        // Borrow it, then force it out of the cache while the lease is still held.
        Assert.True(cache.TryGet(keyA, out var lease));
        Assert.NotNull(lease);

        cache.Clear();

        // The buffer must still be readable through the outstanding lease.
        Assert.Equal(pageBytes, lease!.Page.Pixels.Span.Length);
        _ = lease.Page.Pixels.Span[0];

        // Only after the last lease is released does the buffer go away.
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = pageA.Pixels.Span[0]);
    }

    [Fact]
    public void TestCacheRejectsEntryLargerThanEntireBudget()
    {
        // Regression test: an entry bigger than the whole budget could never satisfy the
        // eviction loop's exit condition, so the cache evicted and disposed EVERY entry and
        // then stored it anyway, staying permanently over budget with a 0% hit rate.
        using var cache = new MultiTierCache(maxMemoryBytes: 100 * 1024);

        var smallKey = new RenderCacheKey("doc1", 1, 96, PageRotation.Rotate0, 1);
        var smallPage = new RenderedPage(1, 10, 10, 40, 96, PageRotation.Rotate0,
            new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(400));
        cache.Put(smallKey, smallPage).Dispose();

        var hugeKey = new RenderCacheKey("doc1", 2, 96, PageRotation.Rotate0, 1);
        var hugePage = new RenderedPage(2, 500, 500, 2000, 96, PageRotation.Rotate0,
            new PdfEngine.Pdfium.Adapters.NativeMemoryOwner(1000 * 1024));

        using (var hugeLease = cache.Put(hugeKey, hugePage))
        {
            // Caller still gets a usable lease...
            Assert.NotNull(hugeLease);
            Assert.Equal(2, hugeLease.Page.PageNumber);
        }

        // ...but it was not cached, the budget was not blown, and the small entry survived.
        Assert.True(cache.CurrentMemoryBytes <= cache.MaxMemoryBytes);
        Assert.True(cache.TryGet(smallKey, out var stillThere));
        stillThere?.Dispose();
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
        Assert.Equal(results[0].Page.WidthPixels, results[1].Page.WidthPixels);

        foreach (var lease in results)
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task TestFailedUndoKeepsCommandOnUndoStack()
    {
        // Regression test: UndoAsync popped BEFORE awaiting, so a failing or cancelled undo
        // lost the command from both stacks - that edit became permanently un-undoable and
        // the history silently diverged from the document.
        var history = new CommandHistory(maxHistory: 10);
        using var session = new DocumentSession();

        bool shouldFailUndo = true;
        var command = new DelegateCommand(
            "Failing Undo",
            _ => ValueTask.CompletedTask,
            _ => shouldFailUndo
                ? throw new InvalidOperationException("undo failed")
                : ValueTask.CompletedTask);

        await history.ExecuteCommandAsync(command, session);
        Assert.True(history.CanUndo);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await history.UndoAsync(session));

        // The command must still be undoable after the failure.
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        shouldFailUndo = false;
        await history.UndoAsync(session);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    private sealed class DelegateCommand : IDocumentCommand
    {
        private readonly Func<DocumentSession, ValueTask> _execute;
        private readonly Func<DocumentSession, ValueTask> _undo;

        public string Name { get; }

        public DelegateCommand(string name, Func<DocumentSession, ValueTask> execute, Func<DocumentSession, ValueTask> undo)
        {
            Name = name;
            _execute = execute;
            _undo = undo;
        }

        public ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default) => _execute(session);
        public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default) => _undo(session);
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
