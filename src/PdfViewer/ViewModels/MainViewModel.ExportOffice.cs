using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Vector.Export;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>An Excel export is running.</summary>
    [ObservableProperty]
    private bool _isExportingExcel;

    /// <summary>A PowerPoint export is running.</summary>
    [ObservableProperty]
    private bool _isExportingPowerPoint;

    private readonly OfficeExportJob _excelExport = new("Excel", ".xlsx", "Excel Workbook (*.xlsx)|*.xlsx");
    private readonly OfficeExportJob _powerPointExport = new("PowerPoint", ".pptx", "PowerPoint Presentation (*.pptx)|*.pptx");

    /// <summary>Chooses where the .xlsx goes (tests set it; null: the save dialog).</summary>
    public Func<string, string?>? ChooseExcelTargetFunc { get; set; }

    /// <summary>Chooses where the .pptx goes (tests set it; null: the save dialog).</summary>
    public Func<string, string?>? ChoosePowerPointTargetFunc { get; set; }

    /// <summary>
    /// Exports the document's tables as an Excel workbook (.xlsx): a sheet a table, with cells
    /// where the table has them, numbers and dates Excel can compute with, and the text around the
    /// tables as rows above and below them. Running it again while it runs cancels it.
    /// </summary>
    [RelayCommand]
    public Task ExportExcelAsync() =>
        RunOfficeExportAsync(_excelExport, ChooseExcelTargetFunc, v => IsExportingExcel = v, async (doc, target, progress, ct) =>
        {
            var result = await Task.Run(() => XlsxExporter.ExportAsync(doc, target, new XlsxExportOptions(), progress, ct), ct);
            return ($"{result.Sheets} sheets, {result.Tables} tables, {result.Numbers + result.Dates} numbers and dates", result.Pages, result.Warnings.Count);
        });

    /// <summary>
    /// Exports the document as a PowerPoint presentation (.pptx): a slide a page, with the page's
    /// text in editable text boxes where it is, its pictures, shapes and tables. Running it again
    /// while it runs cancels it.
    /// </summary>
    [RelayCommand]
    public Task ExportPowerPointAsync() =>
        RunOfficeExportAsync(_powerPointExport, ChoosePowerPointTargetFunc, v => IsExportingPowerPoint = v, async (doc, target, progress, ct) =>
        {
            using var renderer = CreateExportRenderer();
            var result = await Task.Run(() => PptxExporter.ExportAsync(doc, target, new PptxExportOptions { Renderer = renderer }, progress, ct), ct);
            return ($"{result.Slides} slides, {result.TextBoxes} text boxes, {result.Pictures} pictures", result.Pages, result.Warnings.Count);
        });
}
