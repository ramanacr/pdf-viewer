using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Every tab owns a document service, and every service owns a render thread. A tab that is
/// closed must give its thread back: without that, a long session (or the test run, which made
/// about 200 of them) keeps one idle thread per tab ever opened.
/// </summary>
[Collection(MemoryBaselineCollection.Name)]   // counts the process's threads, so it runs alone
public class RenderThreadLifetimeTests : IDisposable
{
    private const int Tabs = 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RenderThreads_" + Guid.NewGuid().ToString("N"));

    public RenderThreadLifetimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static int Threads()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.Threads.Count;
    }

    /// <summary>Threads end shortly after their queue is closed, so the count is polled for a while.</summary>
    private static int ThreadsSettledBelow(int limit)
    {
        int count = Threads();
        var watch = Stopwatch.StartNew();
        while (count >= limit && watch.ElapsedMilliseconds < 10000)
        {
            Thread.Sleep(100);
            count = Threads();
        }
        return count;
    }

    [Fact]
    public async Task ClosedTabs_GiveTheirRenderThreadsBack()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "tabs.pdf"), pageCount: 1);
        using var shell = new ShellViewModel();
        await shell.OpenDocumentAsync(path);   // the first tab stays open throughout
        int before = Threads();

        for (int i = 0; i < Tabs; i++)
        {
            string copy = Path.Combine(_dir, $"tab{i}.pdf");
            File.Copy(path, copy);
            var tab = await shell.OpenDocumentAsync(copy);
            Assert.NotNull(tab);
            Assert.True(await shell.CloseDocumentAsync(tab!));
        }

        int after = ThreadsSettledBelow(before + Tabs / 2);
        Assert.True(after < before + Tabs / 2, $"{Tabs} tabs opened and closed: threads went from {before} to {after}");
    }

    [Fact]
    public void DisposedViewModels_GiveTheirRenderThreadsBack()
    {
        int before = Threads();
        for (int i = 0; i < Tabs; i++)
        {
            using var vm = new MainViewModel();
        }

        int after = ThreadsSettledBelow(before + Tabs / 2);
        Assert.True(after < before + Tabs / 2, $"{Tabs} view models created and disposed: threads went from {before} to {after}");
    }
}
