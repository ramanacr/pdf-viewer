using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pdfium.Native;
using PdfEngine.Safety;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Lists and extracts a document's embedded files.
///
/// Nothing here opens, launches or inspects the payload. It is copied to a path the user
/// chose and left there.
/// </summary>
public sealed class PdfiumAttachmentService : IPdfAttachmentService
{
    /// <summary>
    /// Extensions Windows will run or interpret. Used only to decide whether to warn before
    /// extracting; the real defence is that this application never executes anything.
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".dll", ".sys",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".jar", ".lnk", ".reg", ".inf", ".scf", ".url", ".chm",
        ".docm", ".xlsm", ".pptm", ".dotm", ".xlam", ".iso", ".img"
    };

    /// <summary>A single attachment larger than this is not read into memory in one go.</summary>
    private const long MaxExtractableBytes = 512L * 1024 * 1024;

    public ValueTask<IReadOnlyList<EmbeddedFileInfo>> GetAttachmentsAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        var pdfiumDoc = Require(document);
        cancellationToken.ThrowIfCancellationRequested();

        var files = new List<EmbeddedFileInfo>();

        lock (pdfiumDoc.SyncLock)
        {
            int count = PdfiumNativeBridge.FPDFDoc_GetAttachmentCount(pdfiumDoc.Handle);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IntPtr attachment = PdfiumNativeBridge.FPDFDoc_GetAttachment(pdfiumDoc.Handle, i);
                if (attachment == IntPtr.Zero) continue;

                string name = ReadName(attachment, i);

                files.Add(new EmbeddedFileInfo
                {
                    Index = i,
                    Name = name,
                    SizeBytes = ReadSize(attachment),
                    HasExecutableExtension = IsExecutableName(name)
                });
            }
        }

        return ValueTask.FromResult<IReadOnlyList<EmbeddedFileInfo>>(files);
    }

    public async ValueTask<long> ExtractAttachmentAsync(
        IPdfDocument document,
        int index,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var pdfiumDoc = Require(document);

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));

        cancellationToken.ThrowIfCancellationRequested();

        byte[] payload;

        lock (pdfiumDoc.SyncLock)
        {
            int count = PdfiumNativeBridge.FPDFDoc_GetAttachmentCount(pdfiumDoc.Handle);
            if (index < 0 || index >= count)
                throw new ArgumentOutOfRangeException(nameof(index), $"The document has {count} embedded file(s).");

            IntPtr attachment = PdfiumNativeBridge.FPDFDoc_GetAttachment(pdfiumDoc.Handle, index);
            if (attachment == IntPtr.Zero)
                throw new PdfException($"Embedded file {index + 1} could not be read.");

            if (PdfiumNativeBridge.FPDFAttachment_GetFile(attachment, null, 0, out uint length) == 0)
                throw new PdfException($"Embedded file {index + 1} has no readable contents.");

            if (length > MaxExtractableBytes)
            {
                throw new PdfException(
                    $"Embedded file {index + 1} is {length / (1024 * 1024)} MB, which exceeds the extraction limit.");
            }

            payload = new byte[length];
            if (length > 0 &&
                PdfiumNativeBridge.FPDFAttachment_GetFile(attachment, payload, length, out uint written) == 0)
            {
                throw new PdfException($"Embedded file {index + 1} could not be copied out of the document.");
            }
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Written as plain data. Nothing marks it executable and nothing launches it.
        await File.WriteAllBytesAsync(targetPath, payload, cancellationToken);
        return payload.LongLength;
    }

    private static PdfiumDocument Require(IPdfDocument document)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        return pdfiumDoc;
    }

    private static string ReadName(IntPtr attachment, int index)
    {
        uint len = PdfiumNativeBridge.FPDFAttachment_GetName(attachment, null, 0);
        if (len == 0) return $"(unnamed file {index + 1})";

        byte[] buffer = new byte[len];
        PdfiumNativeBridge.FPDFAttachment_GetName(attachment, buffer, len);
        string name = PdfiumNativeBridge.Utf16BytesToString(buffer, (int)len);

        return string.IsNullOrWhiteSpace(name) ? $"(unnamed file {index + 1})" : name;
    }

    private static long ReadSize(IntPtr attachment) =>
        PdfiumNativeBridge.FPDFAttachment_GetFile(attachment, null, 0, out uint length) != 0 ? length : -1;

    /// <summary>
    /// Looks at the extension only, and at the last one: "invoice.pdf.exe" is an executable,
    /// and a document that names it that way is trying to be read carelessly.
    /// </summary>
    internal static bool IsExecutableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Trailing dots and spaces are stripped by Windows when resolving a name, so
        // "payload.exe . " resolves to an executable. Strip them before looking.
        string trimmed = name.TrimEnd('.', ' ');
        string extension = Path.GetExtension(trimmed);

        return !string.IsNullOrEmpty(extension) && ExecutableExtensions.Contains(extension);
    }
}
