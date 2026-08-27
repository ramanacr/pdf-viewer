using System;
using System.IO;
using System.Windows;
using PdfViewer.Services;
using PdfViewer.Views;

namespace PdfViewer;

public partial class App : Application
{
    public static string? StartupPdfPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize PDFium engine
        PdfiumNativeBridge.EnsureInitialized();

        // Process command line args for opening files directly (e.g. associating with .pdf or CLI)
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            StartupPdfPath = Path.GetFullPath(e.Args[0]);
        }
    }
}
