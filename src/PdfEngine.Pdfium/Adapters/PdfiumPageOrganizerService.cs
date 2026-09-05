using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pages;
using PdfEngine.Pdfium.Native;
using PdfEngine.Rendering;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// High-performance native page manipulation, document assembly, merge, and split service.
/// </summary>
public sealed class PdfiumPageOrganizerService : IPdfPageOrganizerService
{
    public ValueTask RotatePageAsync(
        IPdfDocument document,
        int pageNumber,
        PageRotation newRotation,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (pageNumber < 1 || pageNumber > pdfiumDoc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, pageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid)
                throw new PdfCorruptDocumentException($"Failed to load page {pageNumber} for rotation.");

            int rotValue = (int)newRotation / 90;
            PdfiumNativeBridge.FPDFPage_SetRotation(pageHandle, rotValue);
            PdfiumNativeBridge.FPDFPage_GenerateContent(pageHandle);

            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DeletePageAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (pageNumber < 1 || pageNumber > pdfiumDoc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        if (pdfiumDoc.PageCount <= 1)
            throw new InvalidOperationException("Cannot delete the only remaining page in a document.");

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            PdfiumNativeBridge.FPDFPage_Delete(pdfiumDoc.Handle, pageNumber - 1);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask InsertBlankPageAsync(
        IPdfDocument document,
        int targetIndex,
        double widthPoints = 612,
        double heightPoints = 792,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        targetIndex = Math.Clamp(targetIndex, 0, pdfiumDoc.PageCount);
        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            using var newPage = PdfiumNativeBridge.FPDFPage_New(pdfiumDoc.Handle, targetIndex, widthPoints, heightPoints);
            if (newPage == null || newPage.IsInvalid)
                throw new PdfException("Failed to create new blank page.");

            PdfiumNativeBridge.FPDFPage_GenerateContent(newPage);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask ExtractPagesAsync(
        IPdfDocument document,
        IReadOnlyList<int> pageNumbers,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (pageNumbers == null || pageNumbers.Count == 0)
            throw new ArgumentException("Page numbers cannot be empty.", nameof(pageNumbers));

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            using var newDoc = PdfiumNativeBridge.FPDF_CreateNewDocument();
            if (newDoc == null || newDoc.IsInvalid)
                throw new PdfException("Failed to create destination PDF document.");

            string pageRangeStr = string.Join(",", pageNumbers.Where(p => p >= 1 && p <= pdfiumDoc.PageCount));
            int importResult = PdfiumNativeBridge.FPDF_ImportPages(newDoc, pdfiumDoc.Handle, pageRangeStr, 0);
            if (importResult == 0)
                throw new PdfException($"Failed to extract pages: {pageRangeStr}");

            SaveDocToDisk(newDoc, targetPath);
            return ValueTask.CompletedTask;
        }
    }

    public async ValueTask MergeDocumentsAsync(
        IReadOnlyList<string> sourceFiles,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceFiles == null || sourceFiles.Count < 2)
            throw new ArgumentException("At least 2 PDF documents are required to merge.", nameof(sourceFiles));

        cancellationToken.ThrowIfCancellationRequested();

        // Every native call below is serialized on the global PDFium lock. File I/O stays
        // outside it so we never block other renders while reading from disk (and so we
        // never await while holding a lock).
        SafeDocumentHandle? mergedDoc;
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            mergedDoc = PdfiumNativeBridge.FPDF_CreateNewDocument();
            if (mergedDoc == null || mergedDoc.IsInvalid)
                throw new PdfException("Failed to create merged destination PDF document.");
        }

        try
        {
            int currentInsertIndex = 0;
            int totalFiles = sourceFiles.Count;

            for (int i = 0; i < totalFiles; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string srcFile = sourceFiles[i];

                if (!File.Exists(srcFile))
                    throw new FileNotFoundException($"Source PDF file not found: {srcFile}", srcFile);

                byte[] srcBytes = await File.ReadAllBytesAsync(srcFile, cancellationToken);

                lock (PdfiumNativeBridge.PdfiumLock)
                {
                    IntPtr unmanagedBuf = Marshal.AllocHGlobal(srcBytes.Length);
                    Marshal.Copy(srcBytes, 0, unmanagedBuf, srcBytes.Length);

                    SafeDocumentHandle? srcDoc = null;
                    try
                    {
                        srcDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, srcBytes.Length, null);
                        if (srcDoc == null || srcDoc.IsInvalid)
                            throw new PdfCorruptDocumentException($"Failed to load source document for merge: {srcFile}", srcFile);

                        int srcPageCount = PdfiumNativeBridge.FPDF_GetPageCount(srcDoc);
                        int result = PdfiumNativeBridge.FPDF_ImportPages(mergedDoc, srcDoc, null, currentInsertIndex);
                        if (result == 0)
                            throw new PdfException($"Failed to import pages from {srcFile}");

                        currentInsertIndex += srcPageCount;
                    }
                    finally
                    {
                        // Close the document BEFORE freeing the buffer it parses out of.
                        srcDoc?.Dispose();
                        Marshal.FreeHGlobal(unmanagedBuf);
                    }
                }

                progress?.Report((double)(i + 1) / totalFiles);
            }

            SaveDocToDisk(mergedDoc, targetPath);
        }
        finally
        {
            lock (PdfiumNativeBridge.PdfiumLock)
            {
                mergedDoc.Dispose();
            }
        }
    }

    public async ValueTask SplitDocumentAsync(
        IPdfDocument document,
        IReadOnlyList<int> pageNumbersPerSplit,
        string outputDirectory,
        string filePrefix,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        cancellationToken.ThrowIfCancellationRequested();

        int currentPage = 1;
        int splitIndex = 1;

        foreach (int count in pageNumbersPerSplit)
        {
            if (currentPage > pdfiumDoc.PageCount) break;

            int endPage = Math.Min(pdfiumDoc.PageCount, currentPage + count - 1);
            var pages = Enumerable.Range(currentPage, endPage - currentPage + 1).ToList();

            string targetPath = Path.Combine(outputDirectory, $"{filePrefix}_part{splitIndex:D3}.pdf");
            await ExtractPagesAsync(pdfiumDoc, pages, targetPath, cancellationToken);

            currentPage = endPage + 1;
            splitIndex++;
        }
    }

    private static void SaveDocToDisk(SafeDocumentHandle docHandle, string targetPath)
    {
        // Serialized like every other native entry point; this one previously took no lock.
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            Exception? writeFailure = null;
            var fileWrite = new FPDF_FILEWRITE
            {
                version = 1,
                WriteBlock = (pThis, pData, size) =>
                {
                    // Never unwind a managed exception through PDFium's C++ frames.
                    try
                    {
                        byte[] buffer = new byte[size];
                        Marshal.Copy(pData, buffer, 0, (int)size);
                        outStream.Write(buffer, 0, (int)size);
                        return 1;
                    }
                    catch (Exception ex)
                    {
                        writeFailure ??= ex;
                        return 0;
                    }
                }
            };

            int saveResult = PdfiumNativeBridge.FPDF_SaveAsCopy(docHandle, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
            GC.KeepAlive(fileWrite);

            if (writeFailure != null)
                throw new PdfSaveException("Failed writing PDF file to disk.", writeFailure, targetPath);

            if (saveResult == 0)
            {
                throw new PdfSaveException("Failed to write PDF file to disk.", targetPath);
            }
        }
    }
}
