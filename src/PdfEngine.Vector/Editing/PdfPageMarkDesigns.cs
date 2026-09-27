using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PdfEngine.Vector.Editing;

/// <summary>What a mark is, as Acrobat's Edit PDF tools name them.</summary>
public enum PdfMarkKind { HeaderFooter, Bates, Watermark, Background }

/// <summary>Add puts marks next to those already there; Update replaces every mark of the kind; Remove takes them all away.</summary>
public enum PdfMarkAction { Add, Update, Remove }

public enum PdfPageSubset { All, Odd, Even }

public enum PdfMarkHorizontal { Left, Center, Right }

public enum PdfMarkVertical { Top, Center, Bottom }

/// <summary>What a watermark or background shows.</summary>
public enum PdfMarkSource { Text, Image, Page, Color }

/// <summary>Pages from <paramref name="From"/> to <paramref name="To"/> (1-based, inclusive; null: the last page), all of them or only the odd or even ones.</summary>
public sealed record PdfPageRange(int From = 1, int? To = null, PdfPageSubset Subset = PdfPageSubset.All)
{
    public static PdfPageRange All { get; } = new();

    public IEnumerable<int> Pages(int pageCount)
    {
        int last = Math.Min(To ?? pageCount, pageCount);
        for (int p = Math.Max(1, From); p <= last; p++)
            if (Subset == PdfPageSubset.All || (Subset == PdfPageSubset.Odd) == (p % 2 == 1))
                yield return p;
    }
}

/// <summary>A Bates number: <paramref name="Prefix"/>, the number padded to <paramref name="Digits"/> digits, then <paramref name="Suffix"/>.</summary>
public sealed record PdfBatesNumbering(string Prefix = "", string Suffix = "", long Start = 1, int Digits = 6)
{
    public string Format(long number) =>
        Prefix + Math.Max(0, number).ToString(new string('0', Math.Clamp(Digits, 1, 20)), System.Globalization.CultureInfo.InvariantCulture) + Suffix;
}

/// <summary>
/// Headers and footers: text in six places, each of which may use the tokens
/// <c>&lt;&lt;1&gt;&gt;</c> (page number), <c>&lt;&lt;n&gt;&gt;</c> (page count), <c>&lt;&lt;1 of n&gt;&gt;</c>,
/// <c>&lt;&lt;1/n&gt;&gt;</c>, <c>&lt;&lt;Page 1 of n&gt;&gt;</c>, <c>&lt;&lt;Date&gt;&gt;</c> (in <see cref="DateFormat"/>),
/// a date pattern such as <c>&lt;&lt;mm/dd/yyyy&gt;&gt;</c>, <c>&lt;&lt;File&gt;&gt;</c> (file name) and
/// <c>&lt;&lt;Bates&gt;&gt;</c>. Margins are in points from the edges of the page as it is seen.
/// </summary>
public sealed record PdfHeaderFooter
{
    public string TopLeft { get; init; } = string.Empty;
    public string TopCenter { get; init; } = string.Empty;
    public string TopRight { get; init; } = string.Empty;
    public string BottomLeft { get; init; } = string.Empty;
    public string BottomCenter { get; init; } = string.Empty;
    public string BottomRight { get; init; } = string.Empty;
    public PdfTextFormat Format { get; init; } = new("Arial", 10);
    public double TopMargin { get; init; } = 36;
    public double BottomMargin { get; init; } = 36;
    public double LeftMargin { get; init; } = 72;
    public double RightMargin { get; init; } = 72;
    /// <summary>The number the document's first page gets.</summary>
    public int StartPageNumber { get; init; } = 1;
    /// <summary>A .NET date format for <c>&lt;&lt;Date&gt;&gt;</c>.</summary>
    public string DateFormat { get; init; } = "M/d/yyyy";
    /// <summary>Set for Bates numbering: numbers run over the pages of <see cref="Pages"/> from its start.</summary>
    public PdfBatesNumbering? Bates { get; init; }
    public PdfPageRange Pages { get; init; } = PdfPageRange.All;
    /// <summary>The date the tokens show (now when null).</summary>
    [JsonIgnore] public DateTime? Date { get; init; }
    /// <summary>The name <c>&lt;&lt;File&gt;&gt;</c> shows (the document's own when null).</summary>
    [JsonIgnore] public string? FileName { get; init; }

    [JsonIgnore] public PdfMarkKind Kind => Bates != null ? PdfMarkKind.Bates : PdfMarkKind.HeaderFooter;

    internal IEnumerable<(string Text, PdfMarkHorizontal Horizontal, bool Top)> Boxes()
    {
        yield return (TopLeft, PdfMarkHorizontal.Left, true);
        yield return (TopCenter, PdfMarkHorizontal.Center, true);
        yield return (TopRight, PdfMarkHorizontal.Right, true);
        yield return (BottomLeft, PdfMarkHorizontal.Left, false);
        yield return (BottomCenter, PdfMarkHorizontal.Center, false);
        yield return (BottomRight, PdfMarkHorizontal.Right, false);
    }

    [JsonIgnore] public bool IsEmpty => Boxes().All(b => string.IsNullOrWhiteSpace(b.Text));
}

/// <summary>
/// A watermark or background: text, a picture or a page of another PDF (or, for a background, a
/// colour), placed on the page as it is seen. Offsets are points from the aligned edge towards the
/// middle of the page (from the middle: rightwards and upwards).
/// </summary>
public sealed record PdfWatermark
{
    public PdfMarkSource Source { get; init; } = PdfMarkSource.Text;
    public string Text { get; init; } = string.Empty;
    public PdfTextFormat Format { get; init; } = new("Arial", 48, Red: 0.5, Green: 0.5, Blue: 0.5);
    /// <summary>For <see cref="PdfMarkSource.Image"/>: drawn at one point per pixel before scaling.</summary>
    [JsonIgnore] public PdfImageContent? Image { get; init; }
    /// <summary>For <see cref="PdfMarkSource.Page"/>: the PDF whose page <see cref="SourcePage"/> is drawn.</summary>
    [JsonIgnore] public byte[]? SourcePdf { get; init; }
    public int SourcePage { get; init; } = 1;
    /// <summary>Where the picture or PDF came from (kept for the dialogs; not read here).</summary>
    public string? SourcePath { get; init; }
    /// <summary>For <see cref="PdfMarkSource.Color"/>: RGB, 0..1; it fills the whole page.</summary>
    public double[] Color { get; init; } = { 1, 1, 1 };
    public double Opacity { get; init; } = 1;
    /// <summary>Counter-clockwise, as the page is seen.</summary>
    public double RotationDegrees { get; init; }
    /// <summary>Drawn before the page's content, so the content covers it. A background always is.</summary>
    public bool Behind { get; init; }
    public bool IsBackground { get; init; }
    public PdfMarkHorizontal Horizontal { get; init; } = PdfMarkHorizontal.Center;
    public PdfMarkVertical Vertical { get; init; } = PdfMarkVertical.Center;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    /// <summary>Size relative to the mark's own (the text's size, the picture's pixels, the page's points).</summary>
    public double Scale { get; init; } = 1;
    /// <summary>When set, the mark (turned) is fitted to this fraction of the page's size instead of <see cref="Scale"/>.</summary>
    public double? RelativeScale { get; init; }
    public PdfPageRange Pages { get; init; } = PdfPageRange.All;

    [JsonIgnore] public PdfMarkKind Kind => IsBackground ? PdfMarkKind.Background : PdfMarkKind.Watermark;
    [JsonIgnore] internal bool DrawnBehind => IsBackground || Behind;
}

/// <summary>A mark found on a page. <paramref name="Ours"/>: written by this editor (else by Acrobat or another program).</summary>
public sealed record PdfPageMark(int PageNumber, PdfMarkKind Kind, bool Behind, bool Ours);

/// <summary>The marks a document carries, and the settings the last marks of each kind were made with, when this editor made them.</summary>
public sealed class PdfPageMarksInfo
{
    public required IReadOnlyList<PdfPageMark> Marks { get; init; }
    public PdfHeaderFooter? HeaderFooter { get; init; }
    public PdfHeaderFooter? Bates { get; init; }
    public PdfWatermark? Watermark { get; init; }
    public PdfWatermark? Background { get; init; }

    public bool Has(PdfMarkKind kind) => Marks.Any(m => m.Kind == kind);

    public IReadOnlyList<int> PagesWith(PdfMarkKind kind) => Marks.Where(m => m.Kind == kind).Select(m => m.PageNumber).Distinct().OrderBy(p => p).ToList();
}
