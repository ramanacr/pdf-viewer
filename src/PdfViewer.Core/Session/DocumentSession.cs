using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Documents;
using PdfEngine.Exceptions;

namespace PdfViewer.Core.Session;

/// <summary>
/// First-class document session managing document identity, lifecycle, revisions, and dirty state.
/// </summary>
public sealed class DocumentSession : ObservableObject, IAsyncDisposable, IDisposable
{
    private IPdfDocument? _document;
    private long _revision;
    private bool _isDirty;
    private string _fingerprint = string.Empty;
    private CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// Cancelled when the session closes. Long-running work (renders, prefetches) should
    /// observe this so it stops touching the document before its handles are freed.
    /// </summary>
    public CancellationToken SessionToken => _lifetimeCts.Token;

    public IPdfDocument? Document => _document;
    public bool IsOpen => _document != null && _document.IsOpen;
    public string FilePath => _document?.FilePath ?? string.Empty;
    public DocumentMetadata Metadata => _document?.Metadata ?? new DocumentMetadata();
    public int PageCount => _document?.PageCount ?? 0;
    public string Fingerprint => _fingerprint;

    public long Revision
    {
        get => _revision;
        private set => SetProperty(ref _revision, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public DocumentSession()
    {
    }

    public void AttachDocument(IPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Attaching over an existing document used to silently drop it, leaking its PDFium
        // handle and its backing buffer and keeping the old file locked. Close first.
        if (_document != null)
        {
            Close();
        }

        // Close() cancelled the previous lifetime token; start a fresh one for this document.
        if (_lifetimeCts.IsCancellationRequested)
        {
            _lifetimeCts.Dispose();
            _lifetimeCts = new CancellationTokenSource();
        }

        _document = document;
        _revision = 1;
        _isDirty = false;
        _fingerprint = ComputeFingerprint(document.FilePath);

        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(IsDirty));
    }

    public void IncrementRevision()
    {
        Revision++;
        IsDirty = true;
    }

    public void MarkSaved()
    {
        IsDirty = false;
    }

    public void Close()
    {
        // Signal cancellation BEFORE disposing the document so in-flight renders stop
        // dereferencing handles this method is about to free.
        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }

        if (_document != null)
        {
            _document.Dispose();
            _document = null;
        }

        _fingerprint = string.Empty;
        _revision = 0;
        _isDirty = false;

        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>
    /// Stable per-document cache identity.
    /// Hashes a bounded prefix plus length and last-write time rather than the whole file:
    /// AttachDocument is synchronous and called from the UI thread, so a full SHA-256 of a
    /// large PDF stalled the UI for seconds. Falling back to a random GUID (as the previous
    /// implementation did on every error) also meant cache keys never matched across
    /// attaches, silently reducing the render cache hit rate to zero.
    /// </summary>
    private const int FingerprintPrefixBytes = 1024 * 1024;

    private static string ComputeFingerprint(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            // In-memory document: no stable on-disk identity exists. A fresh GUID is correct
            // here (each such document is genuinely distinct) - it just must not be cached
            // across attaches, which it is not.
            return "mem-" + Guid.NewGuid().ToString("N");
        }

        try
        {
            var info = new FileInfo(filePath);
            using var sha = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            byte[] buffer = new byte[Math.Min(FingerprintPrefixBytes, (int)Math.Min(info.Length, int.MaxValue))];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            sha.TransformBlock(buffer, 0, read, null, 0);
            byte[] tail = System.Text.Encoding.UTF8.GetBytes(
                $"|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{info.FullName.ToLowerInvariant()}");
            sha.TransformFinalBlock(tail, 0, tail.Length);

            return Convert.ToHexString(sha.Hash!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable: derive a deterministic identity from the path so repeated
            // attaches of the same file still share cache entries.
            return "path-" + Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(filePath.ToLowerInvariant())));
        }
    }

    public void Dispose()
    {
        Close();
        _lifetimeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }

        if (_document != null)
        {
            await _document.DisposeAsync();
            _document = null;
        }
        Close();
        _lifetimeCts.Dispose();
    }
}
