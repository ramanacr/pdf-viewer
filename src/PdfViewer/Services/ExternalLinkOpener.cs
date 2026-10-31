using System;
using System.Diagnostics;
using System.Windows;
using PdfEngine.Vector.Services;

namespace PdfViewer.Services;

/// <summary>
/// Asks before a web link in a document is opened, and opens it. The view model only calls it for
/// URIs that <see cref="PdfLinkTargets.TryGetWebUri"/> accepted (http, https, mailto); tests
/// replace it so that no browser is ever started.
/// </summary>
public interface IExternalLinkOpener
{
    /// <summary>Shows the full URL with its host emphasised; true only when the reader chose to open it.</summary>
    bool Confirm(Uri uri, string text);

    /// <summary>Hands the URI to the system's browser or mail program.</summary>
    void Open(Uri uri);
}

/// <summary>The desktop's opener: a confirmation dialog, then the shell.</summary>
public sealed class ShellLinkOpener : IExternalLinkOpener
{
    public bool Confirm(Uri uri, string text)
    {
        var owner = Application.Current?.MainWindow;
        if (owner == null) return false;
        var dialog = new Views.Dialogs.OpenLinkDialog(uri, text) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    public void Open(Uri uri)
    {
        // Checked again here: only an absolute http, https or mailto URI reaches the shell.
        if (!PdfLinkTargets.TryGetWebUri(uri.OriginalString, out var safe)) return;
        Process.Start(new ProcessStartInfo(safe.AbsoluteUri) { UseShellExecute = true });
    }
}
