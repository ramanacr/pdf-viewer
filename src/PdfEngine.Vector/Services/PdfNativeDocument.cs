using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Services;

/// <summary>
/// An open document that can be changed in memory: every edit is committed as an incremental
/// update of <see cref="Bytes"/> and the document re-read from the result, so page count, pages
/// and outline always describe what a save would write. Implements the engine-neutral
/// <see cref="IPdfDocument"/> for the native document services.
/// </summary>
public sealed class PdfNativeDocument : IPdfDocument
{
    private readonly object _sync = new();
    private PdfVectorDocument _vector;
    private bool _disposed;

    private PdfNativeDocument(string filePath, byte[] bytes, PdfVectorDocument vector, string? password)
    {
        FilePath = filePath;
        Bytes = bytes;
        _vector = vector;
        Password = password;
        OriginalBytes = bytes;
    }

    public static async ValueTask<PdfNativeDocument> OpenAsync(string filePath, string? password = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException($"PDF file not found: {filePath}", filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return await OpenAsync(bytes, filePath, password, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<PdfNativeDocument> OpenAsync(byte[] bytes, string filePath = "", string? password = null, CancellationToken cancellationToken = default)
    {
        if (bytes == null || bytes.Length == 0) throw new ArgumentException("PDF bytes cannot be null or empty.", nameof(bytes));
        try
        {
            var vector = await PdfVectorDocument.OpenAsync(bytes, filePath, cancellationToken: cancellationToken, password: password).ConfigureAwait(false);
            return new PdfNativeDocument(filePath, bytes, vector, password);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
        {
            throw new PdfPasswordRequiredException(ex.Message, filePath);
        }
        catch (PdfVectorException ex) when (ex is not PdfEncryptedDocumentException)
        {
            throw new PdfCorruptDocumentException(ex.Message, ex, filePath);
        }
    }

    public string FilePath { get; }

    /// <summary>The current revision: the file as opened plus every committed edit.</summary>
    public byte[] Bytes { get; private set; }

    /// <summary>The bytes the document was opened from.</summary>
    public byte[] OriginalBytes { get; }

    public string? Password { get; }

    public bool IsModified => !ReferenceEquals(Bytes, OriginalBytes);

    /// <summary>The parse of the current revision. Replaced after every edit; do not keep it across one.</summary>
    public PdfVectorDocument Vector
    {
        get
        {
            ThrowIfDisposed();
            return _vector;
        }
    }

    public DocumentMetadata Metadata
    {
        get
        {
            var p = PdfDocumentInfo.ReadProperties(Vector);
            var permissions = Vector.Security is { } s
                ? PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner)
                : PdfDocumentPermissions.Unencrypted;
            return new DocumentMetadata
            {
                Title = p.Title,
                Author = p.Author,
                Subject = p.Subject,
                Keywords = p.Keywords,
                Creator = p.Creator,
                Producer = p.Producer,
                CreationDate = p.CreationDate,
                ModificationDate = p.ModDate,
                PdfVersion = p.HeaderVersion ?? "1.7",
                PageCount = p.PageCount,
                FileSizeBytes = Bytes.LongLength,
                FilePath = FilePath,
                IsEncrypted = p.IsEncrypted,
                PermissionsAllowPrinting = permissions.CanPrint,
                PermissionsAllowCopying = permissions.CanCopy,
                PermissionsAllowModifying = permissions.CanModify,
            };
        }
    }

    public int PageCount => _disposed ? 0 : _vector.PageCount;
    public bool IsOpen => !_disposed;

    /// <summary>
    /// Applies an edit as one incremental update and re-reads the document. Nothing changes when
    /// <paramref name="edit"/> throws or makes no change.
    /// </summary>
    public void Apply(Action<PdfUpdate> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_sync)
        {
            ThrowIfDisposed();
            var update = new PdfUpdate(_vector, Bytes);
            edit(update);
            if (!update.HasChanges) return;
            Replace(update.Commit());
        }
    }

    /// <summary>Replaces the current revision with <paramref name="bytes"/> (an edit made elsewhere on <see cref="Bytes"/>).</summary>
    public void Replace(byte[] bytes)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var reopened = PdfVectorDocument.OpenAsync(bytes, FilePath, password: Password).AsTask().GetAwaiter().GetResult();
            var old = _vector;
            _vector = reopened;
            Bytes = bytes;
            old.Dispose();
        }
    }

    public ValueTask<PageInfo> GetPageInfoAsync(int pageNumber, CancellationToken cancellationToken = default) =>
        Vector.GetPageInfoAsync(pageNumber, cancellationToken);

    public ValueTask<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default) =>
        Vector.GetBookmarksAsync(cancellationToken);

    public ValueTask<IPdfPage> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default) =>
        Vector.GetPageAsync(pageNumber, cancellationToken);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PdfNativeDocument));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _vector.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
