using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Ocr;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Ocr;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>The recognizer to use instead of Windows text recognition (tests).</summary>
    public IOcrEngine? OcrEngineOverride { get; set; }

    /// <summary>Scanned pages are being recognized.</summary>
    [ObservableProperty]
    private bool _isMakingSearchable;

    private CancellationTokenSource? _searchableCts;

    /// <summary>
    /// Makes scanned pages searchable: every page without text is recognized, and the words are
    /// added over it as invisible text, so search, selection and copying work - here and in any
    /// other reader - while the page looks as it did. The result is a revision in memory (saved
    /// incrementally with Save). Running it again while it runs cancels it.
    /// </summary>
    [RelayCommand]
    public async Task MakeSearchableAsync()
    {
        if (IsMakingSearchable)
        {
            _searchableCts?.Cancel();
            return;
        }
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return;
        if (!Permit(DocumentPermissions.CanModify, "adding recognized text")) return;
        if (_docService.IsDecryptedCopy)
        {
            ShowAlert("This document is encrypted for specific recipients; save an unencrypted copy to make it searchable.", "Make Searchable",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var recognizer = OcrEngineOverride ?? (WindowsOcrEngine.IsAvailable ? OcrEngine : null);
        if (recognizer == null)
        {
            ShowAlert("Windows text recognition is not installed. Add an OCR language in Windows Settings > Time & language > Language & region, then try again.",
                "Make Searchable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Pages that already have text are left alone: their text is exact, recognition is not.
        var scanned = new List<int>();
        for (int p = 1; p <= PageCount; p++)
            if (_docService.ExtractPageTextSegments(p).Count == 0) scanned.Add(p);
        if (scanned.Count == 0)
        {
            StatusText = "Every page already has text: nothing to recognize.";
            ShowAlert("Every page of this document already has text, so it is already searchable.", "Make Searchable",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsMakingSearchable = true;
        _searchableCts = new CancellationTokenSource();
        var ct = _searchableCts.Token;
        var recognized = new Dictionary<int, IReadOnlyList<PdfRecognizedWord>>();
        int wordCount = 0;
        try
        {
            byte[] bytes = _docService.CurrentBytes!;
            using var engine = new PdfEngine.Pdfium.PdfiumEngine();
            await using (var pdoc = await engine.OpenDocumentAsync(bytes, _openPassword, ct))
            {
                for (int i = 0; i < scanned.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int page = scanned[i];
                    StatusText = $"Recognizing text: page {page} ({i + 1} of {scanned.Count})... (run again to stop)";
                    var result = await recognizer.RecognizePageAsync(pdoc, page, cancellationToken: ct);
                    var words = result.Words
                        .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Bounds.Width > 0 && w.Bounds.Height > 0)
                        .Select(w => new PdfRecognizedWord(w.Text, w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height))
                        .ToList();
                    if (words.Count > 0) recognized[page] = words;
                    wordCount += words.Count;
                }
            }
            if (recognized.Count == 0)
            {
                StatusText = "No text was recognized on the scanned pages.";
                return;
            }

            byte[] searchable;
            using (var doc = await PdfVectorDocument.OpenAsync(bytes, _docService.CurrentFilePath, password: _openPassword, cancellationToken: ct))
                searchable = PdfTextLayerWriter.Apply(doc, bytes, recognized);
            await _docService.ReloadFromBytesAsync(searchable, ct);
            await LoadFormAsync();

            // The new text is there now: search, selection and read-aloud see it.
            foreach (int page in recognized.Keys)
            {
                var vm = Pages[page - 1];
                vm.TextSegments.Clear();
                vm.IsTextExtracted = false;
            }
            _formChangedSinceSave = true; // an in-memory revision, written as it is on Save
            HasUnsavedChanges = true;
            StatusText = $"Recognized {wordCount} word(s) on {recognized.Count} scanned page(s). The document is now searchable; Save keeps it.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Text recognition stopped; nothing was changed.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = "Text recognition failed.";
            ShowAlert($"The scanned pages could not be made searchable:\n\n{ex.Message}", "Make Searchable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsMakingSearchable = false;
            _searchableCts?.Dispose();
            _searchableCts = null;
        }
    }
}
