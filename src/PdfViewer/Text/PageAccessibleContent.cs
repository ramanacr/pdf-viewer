using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PdfViewer.Text;

/// <summary>What a block of page content is, as assistive technology and read mode present it.</summary>
public enum ContentRole
{
    Heading1, Heading2, Heading3, Heading4, Heading5, Heading6,
    Paragraph,
    ListItem,
    Figure,
    Caption,
    Quote,
    Code,
    TableHeader,
    TableCell,
    Note,
}

/// <summary>One block of a page's content in reading order. Figures carry their alternate text.</summary>
public sealed record ContentBlock(ContentRole Role, string Text, string? AltText = null, string? Language = null)
{
    public bool IsHeading => Role <= ContentRole.Heading6;
    public int HeadingLevel => IsHeading ? (int)Role + 1 : 0;
}

/// <summary>
/// A page's content as a sequence of headings, paragraphs, list items and figures in reading
/// order — what a screen reader reads and what read mode reflows. From the document's tags
/// (ISO 32000-2 14.8, structure tree) when it has them; otherwise inferred from the text layout:
/// paragraphs as the layout finds them, headings by their larger text, list items by their marker.
/// </summary>
public sealed class PageAccessibleContent
{
    public int PageNumber { get; }
    /// <summary>True when the reading order and roles come from the document's structure tree.</summary>
    public bool FromTags { get; }
    public IReadOnlyList<ContentBlock> Blocks { get; }

    public PageAccessibleContent(int pageNumber, bool fromTags, IReadOnlyList<ContentBlock> blocks)
    {
        PageNumber = pageNumber;
        FromTags = fromTags;
        Blocks = blocks;
    }

    public string PlainText => string.Join(Environment.NewLine + Environment.NewLine, Blocks.Select(b => b.Role == ContentRole.Figure ? $"[Figure: {b.AltText ?? "no description"}]" : b.Text));

    /// <summary>Content inferred from an untagged page's text layout.</summary>
    public static PageAccessibleContent FromLayout(int pageNumber, PageTextLayout layout)
    {
        var paragraphs = layout.Paragraphs().ToList();
        if (paragraphs.Count == 0)
            return new PageAccessibleContent(pageNumber, false, Array.Empty<ContentBlock>());

        // Body text size: the thickness most of the page's characters are set in.
        var sizes = new List<(double Size, int Weight)>();
        foreach (var p in paragraphs)
            for (int li = p.FirstLine; li <= p.LastLine; li++)
                sizes.Add((Math.Round(layout.Lines[li].Thickness, 4), layout.Lines[li].End - layout.Lines[li].Start));
        double body = sizes.GroupBy(s => s.Size).OrderByDescending(g => g.Sum(x => x.Weight)).First().Key;

        // Heading levels: distinct sizes clearly above the body, largest first.
        var headingSizes = paragraphs
            .Select(p => AverageThickness(layout, p.FirstLine, p.LastLine))
            .Where(t => t >= body * 1.25)
            .Select(t => Math.Round(t / body, 1))
            .Distinct()
            .OrderByDescending(t => t)
            .ToList();

        var blocks = new List<ContentBlock>();
        foreach (var p in paragraphs)
        {
            // Vertical text: one character per line reads as one word, not spaced letters.
            bool stacked = p.LastLine - p.FirstLine >= 2 &&
                           Enumerable.Range(p.FirstLine, p.LastLine - p.FirstLine + 1).All(li => layout.Lines[li].End - layout.Lines[li].Start <= 1);
            string text = stacked ? Reflow(layout.GetText(p.Start, p.End)).Replace(" ", string.Empty) : Reflow(layout.GetText(p.Start, p.End));
            if (text.Length == 0)
                continue;
            double t = AverageThickness(layout, p.FirstLine, p.LastLine);
            int lines = p.LastLine - p.FirstLine + 1;
            if (t >= body * 1.25 && lines <= 3 && text.Length <= 200)
            {
                int level = Math.Min(6, headingSizes.IndexOf(Math.Round(t / body, 1)) + 1);
                blocks.Add(new ContentBlock((ContentRole)(Math.Max(1, level) - 1), text));
            }
            else if (IsListItem(text))
            {
                blocks.Add(new ContentBlock(ContentRole.ListItem, text));
            }
            else
            {
                blocks.Add(new ContentBlock(ContentRole.Paragraph, text));
            }
        }
        return new PageAccessibleContent(pageNumber, false, MergeStackedCharacters(blocks));
    }

    /// <summary>
    /// Vertical text (a rotated margin note, a spine) comes out of the layout one character per
    /// line: consecutive one-character paragraphs are read as one.
    /// </summary>
    private static List<ContentBlock> MergeStackedCharacters(List<ContentBlock> blocks)
    {
        var result = new List<ContentBlock>(blocks.Count);
        int i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i].Role == ContentRole.Paragraph && blocks[i].Text.Length == 1)
            {
                int j = i;
                var sb = new StringBuilder();
                while (j < blocks.Count && blocks[j].Role == ContentRole.Paragraph && blocks[j].Text.Length == 1)
                    sb.Append(blocks[j++].Text);
                if (j - i >= 3)
                {
                    result.Add(new ContentBlock(ContentRole.Paragraph, sb.ToString()));
                    i = j;
                    continue;
                }
            }
            result.Add(blocks[i++]);
        }
        return result;
    }

    private static double AverageThickness(PageTextLayout layout, int first, int last)
    {
        double sum = 0; int n = 0;
        for (int i = first; i <= last; i++) { sum += layout.Lines[i].Thickness; n++; }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>A paragraph's lines joined into flowing text (line breaks become spaces).</summary>
    internal static string Reflow(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static bool IsListItem(string text) => PageTextLayout.StartsWithListMarker(text);

    /// <summary>The role of a structure element type (standard types, ISO 32000-2 14.8.4); null for groupings.</summary>
    public static ContentRole? RoleForStructureType(string type) => type switch
    {
        "H1" => ContentRole.Heading1,
        "H" or "H2" => ContentRole.Heading2,
        "H3" => ContentRole.Heading3,
        "H4" => ContentRole.Heading4,
        "H5" => ContentRole.Heading5,
        "H6" => ContentRole.Heading6,
        "Title" => ContentRole.Heading1,
        "P" => ContentRole.Paragraph,
        "LI" => ContentRole.ListItem,
        "Figure" or "Formula" => ContentRole.Figure,
        "Caption" => ContentRole.Caption,
        "BlockQuote" or "Quote" => ContentRole.Quote,
        "Code" => ContentRole.Code,
        "TH" => ContentRole.TableHeader,
        "TD" => ContentRole.TableCell,
        "Note" or "FENote" => ContentRole.Note,
        "TOCI" => ContentRole.ListItem,
        _ => null,
    };
}
