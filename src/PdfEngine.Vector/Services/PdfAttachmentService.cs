using System.Globalization;
using System.Xml.Linq;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using PdfEngine.Save;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>Embedded files: the name tree's, then file attachment annotations'. Extraction writes data only.</summary>
public sealed class PdfAttachmentService : IPdfAttachmentService
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".dll", ".sys",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".jar", ".lnk", ".reg", ".inf", ".scf", ".url", ".chm",
        ".docm", ".xlsm", ".pptm", ".dotm", ".xlam", ".iso", ".img"
    };

    public ValueTask<IReadOnlyList<EmbeddedFileInfo>> GetAttachmentsAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        var list = PdfAttachments.List(doc.Vector).Select(a =>
        {
            string name = string.IsNullOrWhiteSpace(a.Name) ? $"(unnamed file {a.Index + 1})" : a.Name;
            return new EmbeddedFileInfo { Index = a.Index, Name = name, SizeBytes = a.Size, HasExecutableExtension = IsExecutableName(name) };
        }).ToList();
        return ValueTask.FromResult<IReadOnlyList<EmbeddedFileInfo>>(list);
    }

    public async ValueTask<long> ExtractAttachmentAsync(IPdfDocument document, int index, string targetPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload;
        try
        {
            payload = PdfAttachments.Extract(doc.Vector, index);
        }
        catch (Exception ex) when (ex is InvalidDataException or PdfEngine.Vector.Diagnostics.PdfVectorException and not Parsing.PdfDataNotLoadedException)
        {
            throw new PdfException($"Embedded file {index + 1} could not be copied out of the document.", ex);
        }
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(targetPath, payload, cancellationToken).ConfigureAwait(false);
        return payload.LongLength;
    }

    /// <summary>Adds a file to the open document (in memory; save to keep it).</summary>
    public ValueTask AddAttachmentAsync(IPdfDocument document, string name, byte[] data, string? description = null, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        doc.Replace(PdfAttachments.Add(doc.Vector, doc.Bytes, name, data, description));
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the file at <paramref name="index"/> from the open document (in memory; save to keep it).</summary>
    public ValueTask DeleteAttachmentAsync(IPdfDocument document, int index, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        doc.Replace(PdfAttachments.Delete(doc.Vector, doc.Bytes, index));
        return ValueTask.CompletedTask;
    }

    /// <summary>The last extension, after Windows strips trailing dots and spaces: "invoice.pdf.exe" is an executable.</summary>
    public static bool IsExecutableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string extension = Path.GetExtension(name.TrimEnd('.', ' '));
        return !string.IsNullOrEmpty(extension) && ExecutableExtensions.Contains(extension);
    }
}
