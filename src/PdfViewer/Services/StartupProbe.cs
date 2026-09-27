using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PdfViewer.ViewModels;

namespace PdfViewer.Services;

/// <summary>
/// Startup instrumentation for the cold-start release gate (eng/releasegate/README.md).
///
/// Active only when the application is launched as
/// <c>PdfViewer.exe --startup-probe &lt;result.json&gt; &lt;document.pdf&gt;</c>: it opens the document
/// the way a double-click would, writes how long each startup milestone took after the process
/// was created, and exits. Nothing is sent anywhere - the result is a local file that the
/// harness (<c>releasegate startup</c>) reads - and without the switch none of this runs.
///
/// Milestones, in milliseconds since the operating system created the process:
///   appStartup         Application.OnStartup entered (runtime, host and WPF are up)
///   windowLoaded       the main window's Loaded event
///   documentOpenStart  the startup document is handed to the shell
///   windowShown        the main window's first frame (ContentRendered)
///   firstPageSurface   page 1 has a vector surface or bitmap to show
///   firstPageRendered  the frame that draws page 1 has been composed and handed to the
///                      compositor (the render pass after the surface arrived has completed)
///
/// windowShown usually lands after documentOpenStart: the document is opened from Loaded,
/// which runs before the first frame.
/// </summary>
internal sealed class StartupProbe
{
    public const string Switch = "--startup-probe";

    /// <summary>A launch that has not drawn page 1 by then is reported as failed rather than hanging the harness.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _resultPath;
    private readonly DateTime _processStartUtc;
    private readonly Dictionary<string, double> _marks = new();
    private bool _finished;
    private PageViewModel? _watchedPage;

    public string DocumentPath { get; }

    /// <summary>
    /// Settings the probe run reads and writes (recent files, the privacy answer), kept beside
    /// the result so a measurement never touches the user's own.
    /// </summary>
    public string SettingsDirectory { get; }

    private StartupProbe(string resultPath, string documentPath)
    {
        _resultPath = resultPath;
        DocumentPath = documentPath;
        SettingsDirectory = Path.Combine(Path.GetDirectoryName(resultPath) ?? Path.GetTempPath(), "probe-settings");

        using var self = Process.GetCurrentProcess();
        _processStartUtc = self.StartTime.ToUniversalTime();
    }

    /// <summary>The probe for these arguments, or null when the application was not launched as one.</summary>
    public static StartupProbe? TryCreate(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase)) return null;
        return new StartupProbe(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
    }

    /// <summary>Records a milestone. Only the first occurrence of each counts.</summary>
    public void Mark(string milestone)
    {
        if (_finished) return;
        _marks.TryAdd(milestone, (DateTime.UtcNow - _processStartUtc).TotalMilliseconds);
    }

    /// <summary>
    /// Starts watching for page 1 of whatever document the shell puts on screen, and arms the
    /// timeout. Call before the document is opened.
    /// </summary>
    public void Watch(Window window, ShellViewModel shell)
    {
        _dispatcher = window.Dispatcher;
        window.ContentRendered += (_, _) => Mark("windowShown");

        var timer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = Timeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Finish($"page 1 was not rendered within {Timeout.TotalSeconds:0} s");
        };
        timer.Start();

        void WatchDocument(MainViewModel? document)
        {
            if (document == null) return;
            document.Pages.CollectionChanged += OnPagesChanged;
            WatchFirstPage(document.Pages.Count > 0 ? document.Pages[0] : null);
        }

        foreach (var document in shell.Documents) WatchDocument(document);
        shell.Documents.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<MainViewModel>() ?? Enumerable.Empty<MainViewModel>())
            {
                WatchDocument(added);
            }
        };
    }

    private void OnPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is System.Collections.ObjectModel.ObservableCollection<PageViewModel> { Count: > 0 } pages)
        {
            WatchFirstPage(pages[0]);
        }
    }

    private void WatchFirstPage(PageViewModel? page)
    {
        if (page == null || page.PageNumber != 1 || ReferenceEquals(page, _watchedPage)) return;
        if (_watchedPage != null) _watchedPage.PropertyChanged -= OnPageChanged;
        _watchedPage = page;
        page.PropertyChanged += OnPageChanged;
        if (page.HasSurface) OnSurface();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.HasSurface) && sender is PageViewModel { HasSurface: true })
        {
            OnSurface();
        }
    }

    private bool _surfaceSeen;
    private Dispatcher? _dispatcher;

    private void OnSurface()
    {
        // The rendering hooks below belong to the window's thread, whichever thread the page changed on.
        if (_dispatcher != null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(OnSurface);
            return;
        }

        if (_surfaceSeen) return;
        _surfaceSeen = true;
        Mark("firstPageSurface");

        // Rendering is raised at the start of the render pass that lays out and draws the
        // changed page; work queued below render priority from inside it runs once that pass
        // has handed the frame to the compositor.
        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            (_dispatcher ?? Dispatcher.CurrentDispatcher).BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                Mark("firstPageRendered");
                Finish(null);
            });
        }

        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>The result file's content; its shape is what <c>releasegate</c>'s ProbeResult.Parse reads.</summary>
    internal string ToJson(string? error)
    {
        var result = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["document"] = DocumentPath,
            ["processStartUtc"] = _processStartUtc.ToString("O"),
            ["engine"] = PdfDocumentServiceFactory.CurrentEngine,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["milestones"] = _marks,
            ["error"] = error,
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Writes the result and ends the process: 0 once page 1 was drawn, 3 otherwise.</summary>
    public void Finish(string? error)
    {
        if (_finished) return;
        _finished = true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_resultPath)!);
            File.WriteAllText(_resultPath, ToJson(error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error ??= ex.Message;
        }

        if (error != null)
        {
            // A modal alert (a document that failed to open) would otherwise keep the process
            // alive: a failed probe exits at once.
            Environment.Exit(3);
        }

        Application.Current?.Shutdown(0);
    }
}
