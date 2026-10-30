using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Files with long chains of incremental updates: every cross-reference section along /Prev counts, and a
/// chain the reader cannot follow to its end (a loop, or past the limits) is rebuilt by scanning the file,
/// never cut short in silence.
/// </summary>
public class XrefChainTests
{
    private const int RedefineAt = 75; // the update that draws page 2 again, with two squares

    /// <summary>
    /// A two-page file whose pages and content exist only in the first revision, followed by
    /// <paramref name="updates"/> incremental updates that each add one object. Update
    /// <see cref="RedefineAt"/> (when reached) replaces the content of page 2. Every /Prev value is
    /// written with 10 digits, so a test can patch it in place.
    /// </summary>
    private static (byte[] Pdf, List<long> Sections) Build(int updates)
    {
        var sb = new StringBuilder();
        var sections = new List<long>();
        sb.Append("%PDF-1.7\n");
        var offsets = new long[6];
        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 100 100] >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>");
        Obj(4, "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>");
        Obj(5, Stream("1 0 0 rg 10 10 50 50 re f"));

        long xref = sb.Length;
        sections.Add(xref);
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        // Page 2 gets its own content object at the redefinition, so page 1 keeps the original one.
        int next = 6;
        for (int u = 1; u <= updates; u++)
        {
            var objs = new List<(int Num, long Offset)>();
            int added = next++;
            objs.Add((added, sb.Length));
            sb.Append(added).Append(" 0 obj\n<< /Revision ").Append(u).Append(" >>\nendobj\n");
            if (u == RedefineAt)
            {
                int content = next++;
                objs.Add((content, sb.Length));
                sb.Append(content).Append(" 0 obj\n").Append(Stream("0 1 0 rg 10 10 20 20 re f 10 40 20 20 re f")).Append("\nendobj\n");
                objs.Add((4, sb.Length));
                sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /Contents ").Append(content).Append(" 0 R >>\nendobj\n");
            }

            long prev = sections[^1];
            long section = sb.Length;
            sections.Add(section);
            sb.Append("xref\n");
            foreach (var (num, off) in objs.OrderBy(o => o.Num))
                sb.Append(num).Append(" 1\n").Append(off.ToString("D10")).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(next).Append(" /Root 1 0 R /Prev ").Append(prev.ToString("D10"))
              .Append(" >>\nstartxref\n").Append(section).Append("\n%%EOF\n");
        }
        return (Encoding.Latin1.GetBytes(sb.ToString()), sections);
    }

    private static async Task AssertAllPagesAndContent(PdfVectorDocument doc, bool redefined)
    {
        Assert.Equal(2, doc.PageCount);
        Assert.Single((await doc.GetPageDisplayListAsync(1)).Commands.OfType<FillPath>());
        Assert.Equal(redefined ? 2 : 1, (await doc.GetPageDisplayListAsync(2)).Commands.OfType<FillPath>().Count());
    }

    [Fact]
    public async Task ManyIncrementalUpdates_ContentOfTheFirstRevisionIsKept()
    {
        var (pdf, _) = Build(150);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.False(doc.WasRepaired); // a normal chain: read through /Prev, not rebuilt
        await AssertAllPagesAndContent(doc, redefined: true);
        Assert.NotNull(doc.Resolver.Resolve(6)); // defined by the oldest update
    }

    [Fact]
    public async Task PrevLoop_DoesNotHangAndIsRepaired()
    {
        var (pdf, sections) = Build(3);
        // The first update's /Prev points at the newest section: newest -> 2 -> 1 -> newest.
        string text = Encoding.Latin1.GetString(pdf);
        string from = "/Prev " + sections[0].ToString("D10");
        Assert.Equal(1, CountOf(text, from));
        pdf = Encoding.Latin1.GetBytes(text.Replace(from, "/Prev " + sections[^1].ToString("D10")));

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.True(doc.WasRepaired);
        await AssertAllPagesAndContent(doc, redefined: false);
    }

    [Fact]
    public async Task SelfReferencingPrev_IsRepaired()
    {
        var (pdf, sections) = Build(2);
        string text = Encoding.Latin1.GetString(pdf);
        string from = "/Prev " + sections[1].ToString("D10");
        pdf = Encoding.Latin1.GetBytes(text.Replace(from, "/Prev " + sections[2].ToString("D10")));

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.True(doc.WasRepaired);
        await AssertAllPagesAndContent(doc, redefined: false);
    }

    [Fact]
    public async Task ChainPastTheDefaultLimit_IsRepairedNotTruncated()
    {
        int updates = PdfSecurityLimits.Default.MaxTrailerChainDepth + 10;
        var (pdf, _) = Build(updates);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.True(doc.WasRepaired);
        await AssertAllPagesAndContent(doc, redefined: true);
        Assert.NotNull(doc.Resolver.Resolve(6));
    }

    [Fact]
    public async Task ChainPastALowerSectionLimit_IsRepairedNotTruncated()
    {
        var (pdf, _) = Build(RedefineAt + 25);
        var limits = new PdfSecurityLimits { MaxTrailerChainDepth = 20 };
        using var doc = await PdfVectorDocument.OpenAsync(pdf, limits: limits);
        Assert.True(doc.WasRepaired);
        await AssertAllPagesAndContent(doc, redefined: true);
    }

    [Fact]
    public async Task ChainPastTheEntryBudget_IsRepairedNotTruncated()
    {
        var (pdf, _) = Build(RedefineAt + 25);
        var limits = new PdfSecurityLimits { MaxTrailerChainEntries = 50 };
        using var doc = await PdfVectorDocument.OpenAsync(pdf, limits: limits);
        Assert.True(doc.WasRepaired);
        await AssertAllPagesAndContent(doc, redefined: true);
    }

    [Fact]
    public async Task ChainWithinTheLimits_IsNotRepaired()
    {
        var (pdf, _) = Build(RedefineAt + 25);
        var limits = new PdfSecurityLimits { MaxTrailerChainDepth = RedefineAt + 26 };
        using var doc = await PdfVectorDocument.OpenAsync(pdf, limits: limits);
        Assert.False(doc.WasRepaired);
        await AssertAllPagesAndContent(doc, redefined: true);
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
