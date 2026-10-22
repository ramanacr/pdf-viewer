using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;

namespace PdfViewer.Services;

/// <summary>The Office application that opens a kind of file.</summary>
public enum OfficeApp
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>Converts Office and other documents to PDF with the application that opens them.</summary>
public interface IOfficeConverter
{
    /// <summary>The application that would convert a file of this extension, whether or not it is installed.</summary>
    OfficeApp? AppFor(string extension);

    /// <summary>The application is installed (registered for automation).</summary>
    bool IsInstalled(OfficeApp app);

    /// <summary>Writes <paramref name="source"/> as a PDF at <paramref name="targetPdf"/>.</summary>
    Task ConvertAsync(string source, string targetPdf, CancellationToken cancellationToken = default);
}

/// <summary>The Office application could not convert the file; the message says why, without its content.</summary>
public sealed class OfficeConversionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Microsoft Office's own PDF export (ExportAsFixedFormat) through COM, on this machine: nothing
/// is uploaded. Each conversion opens the file read-only (not added to recent files, links not
/// updated, macros disabled, no repair) in a hidden instance, exports it, and closes it without
/// saving. Office's settings are never changed: only the instance's own alerts are turned off.
/// The instance is quit only if this conversion started it; a PowerPoint the user already has
/// open is used and left open, with only the converted presentation closed.
/// </summary>
public sealed class OfficeConverter : IOfficeConverter
{
    private static readonly Dictionary<string, OfficeApp> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = OfficeApp.Word, [".docx"] = OfficeApp.Word, [".docm"] = OfficeApp.Word, [".dot"] = OfficeApp.Word, [".dotx"] = OfficeApp.Word,
        [".dotm"] = OfficeApp.Word, [".rtf"] = OfficeApp.Word, [".odt"] = OfficeApp.Word, [".txt"] = OfficeApp.Word, [".htm"] = OfficeApp.Word,
        [".html"] = OfficeApp.Word, [".mht"] = OfficeApp.Word, [".mhtml"] = OfficeApp.Word, [".wpd"] = OfficeApp.Word, [".wps"] = OfficeApp.Word,
        [".xls"] = OfficeApp.Excel, [".xlsx"] = OfficeApp.Excel, [".xlsm"] = OfficeApp.Excel, [".xlsb"] = OfficeApp.Excel, [".xltx"] = OfficeApp.Excel,
        [".csv"] = OfficeApp.Excel, [".ods"] = OfficeApp.Excel,
        [".ppt"] = OfficeApp.PowerPoint, [".pptx"] = OfficeApp.PowerPoint, [".pptm"] = OfficeApp.PowerPoint, [".pps"] = OfficeApp.PowerPoint,
        [".ppsx"] = OfficeApp.PowerPoint, [".potx"] = OfficeApp.PowerPoint, [".odp"] = OfficeApp.PowerPoint,
    };

    /// <summary>The file types offered for conversion.</summary>
    public static IReadOnlyCollection<string> SupportedExtensions => Extensions.Keys;

    public OfficeApp? AppFor(string extension) => Extensions.TryGetValue(extension, out var app) ? app : null;

    public bool IsInstalled(OfficeApp app) => OperatingSystem.IsWindows() && Type.GetTypeFromProgID(ProgId(app)) != null;

    private static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    private static string ProcessName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "WINWORD",
        OfficeApp.Excel => "EXCEL",
        _ => "POWERPNT",
    };

    public Task ConvertAsync(string source, string targetPdf, CancellationToken cancellationToken = default)
    {
        var app = AppFor(Path.GetExtension(source)) ?? throw new OfficeConversionException("Office does not open this kind of file.");
        if (!IsInstalled(app)) throw new OfficeConversionException($"Microsoft {app} is not installed, so this file cannot be converted.");
        string full = Path.GetFullPath(source), target = Path.GetFullPath(targetPdf);
        cancellationToken.ThrowIfCancellationRequested();
        return StaWorker.Run(() =>
        {
            Convert(app, full, target, cancellationToken);
            return true;
        });
    }

    private static HashSet<int> Running(OfficeApp app) => Process.GetProcessesByName(ProcessName(app)).Select(p => { using (p) return p.Id; }).ToHashSet();

    private static void Convert(OfficeApp app, string source, string target, CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID(ProgId(app))!;
        var before = Running(app);
        dynamic office;
        try
        {
            office = Activator.CreateInstance(type)!;
        }
        catch (COMException ex)
        {
            throw new OfficeConversionException($"Microsoft {app} could not be started.", ex);
        }
        var started = Running(app).Except(before).ToList();
        bool ours = started.Count > 0;
        dynamic? document = null;
        try
        {
            if (ours)
            {
                // This instance's own state only; none of it is saved as a setting.
                if (app == OfficeApp.Excel)
                {
                    office.Visible = false;
                    office.DisplayAlerts = false;
                }
                else if (app == OfficeApp.Word)
                {
                    office.Visible = false;
                    office.DisplayAlerts = 0; // wdAlertsNone
                }
                else office.DisplayAlerts = 1; // ppAlertsNone
                office.AutomationSecurity = 3; // msoAutomationSecurityForceDisable: the file's macros never run
            }
            ct.ThrowIfCancellationRequested();
            switch (app)
            {
                case OfficeApp.Word:
                    document = office.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                        PasswordDocument: "", Revert: false, Visible: false, OpenAndRepair: false, NoEncodingDialog: true);
                    ct.ThrowIfCancellationRequested();
                    // wdExportFormatPDF, for print, the whole document, headings as bookmarks, tagged.
                    document.ExportAsFixedFormat(OutputFileName: target, ExportFormat: 17, OpenAfterExport: false, OptimizeFor: 0, Range: 0,
                        Item: 0, IncludeDocProps: true, KeepIRM: true, CreateBookmarks: 1, DocStructureTags: true, BitmapMissingFonts: true, UseISO19005_1: false);
                    break;
                case OfficeApp.Excel:
                    document = office.Workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true, Password: "", IgnoreReadOnlyRecommended: true,
                        Notify: false, AddToMru: false, CorruptLoad: 0);
                    ct.ThrowIfCancellationRequested();
                    // xlTypePDF, standard quality, the print areas as set.
                    document.ExportAsFixedFormat(Type: 0, Filename: target, Quality: 0, IncludeDocProperties: true, IgnorePrintAreas: false, OpenAfterPublish: false);
                    break;
                default:
                    // Read-only, not untitled, no window.
                    document = office.Presentations.Open(source, -1, 0, 0);
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        document.ExportAsFixedFormat(target, 2, 2); // ppFixedFormatTypePDF, ppFixedFormatIntentPrint
                    }
                    catch (Exception ex) when (ex is COMException or RuntimeBinderException or ArgumentException)
                    {
                        document.SaveCopyAs(target, 32); // ppSaveAsPDF: the same export, the presentation itself untouched
                    }
                    break;
            }
            if (!File.Exists(target)) throw new OfficeConversionException($"Microsoft {app} did not write the PDF.");
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException)
        {
            throw new OfficeConversionException($"Microsoft {app} could not convert the file (it may be password-protected, damaged or open elsewhere).", ex);
        }
        finally
        {
            try
            {
                if (document != null)
                {
                    if (app == OfficeApp.Word) document.Close(0); // wdDoNotSaveChanges
                    else if (app == OfficeApp.Excel) document.Close(false);
                    else document.Close();
                }
            }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
            Release(document);
            if (ours)
            {
                try { office.Quit(); } catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
            }
            Release(office);
            // The wrappers hold the instance open until they are collected.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (int id in started)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    if (!process.WaitForExit(15_000)) process.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static void Release(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
        catch (ArgumentException) { }
    }
}
