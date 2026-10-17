using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>A rectangle on the page as it is shown: points from the top-left corner, y down.</summary>
internal readonly record struct Box(double L, double T, double R, double B)
{
    public double W => R - L;
    public double H => B - T;
    public double Cx => (L + R) / 2;
    public double Cy => (T + B) / 2;
    public bool IsEmpty => W <= 0 || H <= 0;
    public Box Union(Box o) => new(Math.Min(L, o.L), Math.Min(T, o.T), Math.Max(R, o.R), Math.Max(B, o.B));
    public double OverlapX(Box o) => Math.Max(0, Math.Min(R, o.R) - Math.Max(L, o.L));
    public double OverlapY(Box o) => Math.Max(0, Math.Min(B, o.B) - Math.Max(T, o.T));
    public double Overlap(Box o) => OverlapX(o) * OverlapY(o);
    public bool Contains(double x, double y) => x >= L && x <= R && y >= T && y <= B;
    public Box Inflate(double d) => new(L - d, T - d, R + d, B + d);
}

/// <summary>A run of text with one look.</summary>
internal sealed class DocRun
{
    public string Text = string.Empty;
    public string Font = "Arial";
    public double Size = 11;
    public bool Bold, Italic, Underline, Strike, Rtl, EastAsian;
    public string Color = "000000";
    /// <summary>1 superscript, -1 subscript.</summary>
    public int VertAlign;
    /// <summary>Horizontal scaling in percent.</summary>
    public int Scale = 100;
    /// <summary>Index into the document's hyperlinks, or -1.</summary>
    public int Link = -1;
    /// <summary>A field instead of text: PAGE or NUMPAGES.</summary>
    public string? Field;
    /// <summary>Space added after each character, in points (negative: condensed, so a line fits as it does on the page).</summary>
    public double Spacing;

    public DocRun Clone(string text) => new()
    {
        Text = text, Font = Font, Size = Size, Bold = Bold, Italic = Italic, Underline = Underline, Strike = Strike, Rtl = Rtl,
        EastAsian = EastAsian, Color = Color, VertAlign = VertAlign, Scale = Scale, Link = Link, Field = Field, Spacing = Spacing,
    };

    public bool SameLook(DocRun o) => Font == o.Font && Math.Abs(Size - o.Size) < 0.26 && Bold == o.Bold && Italic == o.Italic && Underline == o.Underline
        && Strike == o.Strike && Rtl == o.Rtl && EastAsian == o.EastAsian && Color == o.Color && VertAlign == o.VertAlign && Scale == o.Scale && Link == o.Link
        && Field == null && o.Field == null && Math.Abs(Spacing - o.Spacing) < 0.01;
}

/// <summary>A line of a paragraph as the page shows it.</summary>
internal sealed class DocLine
{
    public Box Box;
    public double Baseline;
    public double Size;
}

/// <summary>Something in the flow of text: a paragraph or a table.</summary>
internal abstract class DocItem
{
    public Box Box;
    /// <summary>Starts a new page (it is the first thing of a page after the first).</summary>
    public bool PageBreakBefore;
    /// <summary>Starts the next column of a multi-column section.</summary>
    public bool ColumnBreakBefore;
    /// <summary>Space above it, in points.</summary>
    public double SpaceBefore;
    /// <summary>Distance of its left edge from the left edge of its column, in points.</summary>
    public double Indent;
    /// <summary>The last thing on its page: it does not keep with the first thing of the next page.</summary>
    public bool LastOnPage;
}

internal enum TabKind { Left, Center, Right, RightDots }

/// <summary>What makes a paragraph an item of a list.</summary>
internal sealed class ListMark
{
    /// <summary>bullet, decimal, lowerLetter, upperLetter, lowerRoman, upperRoman.</summary>
    public string Format = "bullet";
    /// <summary>The bullet character, or the number's pattern around %1 ("%1.", "(%1)").</summary>
    public string Text = "•";
    public int Value = 1;
    public int Level;
    /// <summary>Where the marker and the text after it start (x on the page).</summary>
    public double MarkerX, TextX;
    public DocRun? Look;
    /// <summary>The list's numbering instance (w:num), set when lists are grouped.</summary>
    public int NumId;
    /// <summary>Characters of the paragraph's text the marker takes, with the space after it.</summary>
    public int Length;
    /// <summary>A hyphen or asterisk: a list only when more items follow.</summary>
    public bool Weak;
    /// <summary>The tags say the paragraph is a list item.</summary>
    public bool Tagged;
    /// <summary>The number as written (a letter or roman numeral too).</summary>
    public string Token = string.Empty;
}

internal sealed class DocParagraph : DocItem
{
    public List<DocRun> Runs = new();
    /// <summary>Where each character of <see cref="Text"/> starts on the page (x), NaN for spaces put between words.</summary>
    public List<double> CharX = new();
    /// <summary>The line (index into <see cref="Lines"/>) each character of <see cref="Text"/> is on.</summary>
    public List<int> CharLine = new();
    public List<DocLine> Lines = new();
    public PdfTextAlignment Align;
    /// <summary>Alignment worked out from its lines (several lines), not guessed from one.</summary>
    public bool AlignKnown;
    public bool Rtl, Vertical, Rotated;
    /// <summary>Turned text: the direction it reads in (degrees clockwise on the page) and its length that way.</summary>
    public double Angle, Along;
    /// <summary>Distance between baselines.</summary>
    public double Pitch;
    public int Heading;
    /// <summary>From the structure tree: a heading level (1-6), or a list item at this depth (0 = not one).</summary>
    public int TagHeading, TagListDepth;
    public bool TagListItem;
    public string? TagListNumbering;
    public ListMark? List;
    public string? Shading;
    public List<(double X, TabKind Kind)> Tabs = new();
    /// <summary>An image shown in the flow, on a line of its own.</summary>
    public DocImage? Image;
    /// <summary>Images placed on the page at fixed positions, anchored to this paragraph.</summary>
    public List<DocImage> Anchors = new();
    /// <summary>Text boxes and drawn shapes at fixed positions on the page, anchored to this paragraph.</summary>
    public List<DocTextBox> TextBoxes = new();
    public List<DocShape> Shapes = new();
    /// <summary>Its lines break for good (a line of an address or a list) rather than wrap.</summary>
    public bool HardLines;
    /// <summary>In a header or footer: made of what repeats there.</summary>
    public bool Repeated;

    // Worked out by the layout.
    public double RightIndent, FirstLine, LineHeight;
    /// <summary>Width of the column, cell or text box it is laid out in.</summary>
    public double ColumnWidth;

    public string Text => string.Concat(Runs.Select(r => r.Text));
    public double Size => Lines.Count > 0 ? Lines.Max(l => l.Size) : Runs.Count > 0 ? Runs.Max(r => r.Size) : 11;
    public double FirstBaseline => Lines.Count > 0 ? Lines[0].Baseline : Box.B;
    public double LastBaseline => Lines.Count > 0 ? Lines[^1].Baseline : Box.B;
    public bool IsEmpty => Image == null && Runs.All(r => r.Field == null && r.Text.Trim().Length == 0);
}

internal sealed class DocImage
{
    /// <summary>Relationship id of the image part.</summary>
    public string RelId = string.Empty;
    public Box Box;
    public bool Anchored, Behind;
    /// <summary>Rotation in whole degrees clockwise, and mirroring.</summary>
    public int Rotation;
    public bool FlipH, FlipV;
    /// <summary>What is cut off each side (fractions of the picture), when it runs off the page.</summary>
    public double CropL, CropT, CropR, CropB;
    /// <summary>The picture's own size before rotation, in points.</summary>
    public double Width, Height;
    public int Id;
}

/// <summary>A text box at a fixed place on the page: turned text, or a header or footer that must not take room from the body.</summary>
internal sealed class DocTextBox
{
    /// <summary>Where it is on the page as shown (its bounds after turning).</summary>
    public Box Box;
    /// <summary>Its own size before it is turned, in points.</summary>
    public double Width, Height;
    /// <summary>Degrees clockwise.</summary>
    public int Rotation;
    public List<DocParagraph> Paragraphs = new();
}

internal enum ShapeKind { Rectangle, Line, Picture }

/// <summary>A drawing of the page as a Word shape (a rectangle or a line), or a picture of a group of drawings.</summary>
internal sealed class DocShape
{
    public ShapeKind Kind;
    /// <summary>Its bounds on the page as shown.</summary>
    public Box Box;
    /// <summary>A line from the box's top-right corner (FlipH) or bottom-left corner (FlipV) rather than its top-left.</summary>
    public bool FlipH, FlipV;
    public string? Fill, Stroke;
    public double StrokeWidth;
    /// <summary>Behind the text.</summary>
    public bool Behind;
    /// <summary>A picture: its image part.</summary>
    public string RelId = string.Empty;
    /// <summary>Its place in the page's drawing order.</summary>
    public int Order;
}

internal sealed record DocBorder(double Width, string Color);

internal sealed class DocCell
{
    public int Row, Col, RowSpan = 1, ColSpan = 1;
    public List<DocParagraph> Paragraphs = new();
    public string? Shading;
    public DocBorder? Top, Left, Bottom, Right;
    /// <summary>top, center or bottom.</summary>
    public string VAlign = "top";
    public bool Header;
    public Box Box;
    /// <summary>A layout cell holding tables (side by side with others), in order; null for a cell of text.</summary>
    public List<DocItem>? Items;

    public IEnumerable<DocParagraph> AllParagraphs => Items == null ? Paragraphs
        : Items.SelectMany(i => i is DocTable t ? t.Cells.SelectMany(c => c.AllParagraphs) : i is DocParagraph p ? new[] { p } : Array.Empty<DocParagraph>());
}

internal sealed class DocTable : DocItem
{
    /// <summary>Column boundaries (x) and row boundaries (y), in order.</summary>
    public double[] Cols = Array.Empty<double>();
    public double[] Rows = Array.Empty<double>();
    public List<DocCell> Cells = new();
    public bool HeaderRow;
}

/// <summary>One page's content, before it is laid out as Word sections.</summary>
internal sealed class DocPage
{
    public int Number;
    public double Width, Height;
    public List<DocItem> Items = new();
    public List<DocImage> Floating = new();
    public List<DocTextBox> TextBoxes = new();
    public List<DocShape> Shapes = new();
    /// <summary>Most of its text is written in columns top to bottom (East Asian vertical writing).</summary>
    public bool VerticalWriting;
    /// <summary>Header and footer paragraphs taken out of the page (they repeat on many pages).</summary>
    public List<DocParagraph> Header = new(), Footer = new();
    /// <summary>Which of the document's headers and footers the page has (-1: none).</summary>
    public int HeaderVariant = -1, FooterVariant = -1;
    /// <summary>The page number its header or footer prints, and the number's format (decimal, lowerRoman, upperRoman).</summary>
    public int? PageValue;
    public string NumberFormat = "decimal";

    public IEnumerable<DocParagraph> Paragraphs => Items.OfType<DocParagraph>();
}

/// <summary>A Word section: page setup and columns, and what it holds.</summary>
internal sealed class DocSection
{
    public double Width, Height;
    public double MarginL, MarginT, MarginR, MarginB, HeaderDistance = 36, FooterDistance = 36;
    /// <summary>Column widths and the space after each (all but the last); one entry for a single column.</summary>
    public List<(double Width, double Space)> Columns = new();
    public bool Continuous, VerticalWriting;
    public int HeaderVariant = -1, FooterVariant = -1;
    /// <summary>The number its first page has, when it starts the page numbers again.</summary>
    public int? PageStart;
    public string NumberFormat = "decimal";
    public List<DocItem> Items = new();

    public bool SameSetup(DocSection o) => HeaderVariant == o.HeaderVariant && FooterVariant == o.FooterVariant && NumberFormat == o.NumberFormat
        && Math.Abs(Width - o.Width) < 1 && Math.Abs(Height - o.Height) < 1 && Math.Abs(MarginL - o.MarginL) < 1
        && Math.Abs(MarginR - o.MarginR) < 1 && Math.Abs(MarginT - o.MarginT) < 1 && Math.Abs(MarginB - o.MarginB) < 1
        && VerticalWriting == o.VerticalWriting && Columns.Count == o.Columns.Count
        && Columns.Zip(o.Columns).All(p => Math.Abs(p.First.Width - p.Second.Width) < 4 && Math.Abs(p.First.Space - p.Second.Space) < 4);
}

/// <summary>A header or footer: its paragraphs, in flow or, where body text runs into its band, in text boxes.</summary>
internal sealed class DocHeaderFooter
{
    public List<DocParagraph> Paragraphs = new();
    public bool Floating;
    public List<DocTextBox> Boxes = new();
    /// <summary>The page the paragraphs were taken from.</summary>
    public DocPage? Page;
}

/// <summary>A list (w:num): the marker of each of its levels, from its first item there.</summary>
internal sealed class ListDef
{
    public int NumId;
    public SortedDictionary<int, ListMark> Levels = new();
}

/// <summary>Everything the writer needs.</summary>
internal sealed class DocDocument
{
    public List<DocSection> Sections = new();
    public List<ListDef> Lists = new();
    public List<string> Hyperlinks = new();
    /// <summary>The headers and footers the pages have (they can differ: odd and even pages, front matter, parts).</summary>
    public List<DocHeaderFooter> Headers = new(), Footers = new();
    public string BodyFont = "Calibri";
    /// <summary>What each font family is like, for Word to stand in a similar one where it is not installed.</summary>
    public Dictionary<string, (bool Serif, bool Fixed)> FontKinds = new(StringComparer.Ordinal);
    public double BodySize = 11;
    public string? Title;
}

internal static class TextKinds
{
    public static bool IsRtl(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    public static bool IsEastAsian(char c) => c is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '鿿' or >= 'ꥠ' and <= '꥿'
        or >= '가' and <= '퟿' or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '￯';

    /// <summary>Characters XML cannot hold are left out.</summary>
    public static string Clean(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = c == '\t' || c >= 0x20 && c < 0xD800 || c >= 0xE000 && c <= 0xFFFD
                || char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            if (char.IsLowSurrogate(c)) ok = i > 0 && char.IsHighSurrogate(s[i - 1]);
            if (ok) { sb?.Append(c); continue; }
            sb ??= new StringBuilder(s, 0, i, s.Length);
        }
        return sb?.ToString() ?? s;
    }
}
