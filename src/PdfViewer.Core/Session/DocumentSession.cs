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
        _document = document ?? throw new ArgumentNullException(nameof(document));
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

    private static string ComputeFingerprint(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return Guid.NewGuid().ToString("N");

        try
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    public void Dispose()
    {
        Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_document != null)
        {
            await _document.DisposeAsync();
            _document = null;
        }
        Close();
    }
}
