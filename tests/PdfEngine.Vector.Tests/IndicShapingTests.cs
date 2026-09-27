using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Shaping Indic scripts in new text: syllables, reph, pre-base matras, conjuncts and mark
/// positioning, compared with what Windows' own shaping engine (Uniscribe) makes of the same text
/// in the same installed font. Each test returns early when Nirmala UI is not installed.
/// </summary>
public class IndicShapingTests
{
    private readonly ITestOutputHelper _output;

    public IndicShapingTests(ITestOutputHelper output) => _output = output;

    private static TrueTypeFontFile? Nirmala()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "Nirmala.ttc");
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        for (int i = 0; i < TrueTypeFontFile.FontCount(data); i++)
            if (TrueTypeFontFile.TryLoad(data, i) is { FamilyName: "Nirmala UI", IsBold: false, Weight: 400 } font) return font;
        return null;
    }

    private static List<TextShaper.Shaped> Shape(TrueTypeFontFile font, string text) => TextShaper.ShapeLine(text, _ => font, f => f).Select(g => g.Glyph).ToList();

    [Fact]
    public void Syllables_FollowTheSpecification()
    {
        if (Nirmala() is not { } font) return;
        // क्षि: ka, virama, ssa, i matra. The i matra is written after the cluster and drawn before it.
        var kshi = Shape(font, "क्षि");
        Assert.Equal(2, kshi.Count);
        Assert.Equal("ि", kshi[0].Text);
        Assert.Equal("क्ष", kshi[1].Text); // the conjunct is one glyph, and reads as its three characters

        // र्क: the Ra and virama become a reph, drawn on the consonant after them.
        var rka = Shape(font, "र्क");
        Assert.Equal(2, rka.Count);
        Assert.Equal("क", rka[0].Text);
        Assert.Equal("र्", rka[1].Text);
        Assert.NotEqual(font.GlyphFor('र'), rka[1].Gid);
        Assert.Equal(0, rka[1].Advance ?? font.Advance(rka[1].Gid)); // the reph does not advance: it sits on the ka

        // हिन्दी: the i matra before ha; na and virama as a half form (or a ligature with da); the ii matra last.
        var hindi = Shape(font, "हिन्दी");
        Assert.Equal("ि", hindi[0].Text);
        Assert.Equal("ह", hindi[1].Text);
        Assert.Equal("ी", hindi[^1].Text);
        Assert.Equal("हिन्दी".Length, hindi.Sum(g => g.Text.Length));
        Assert.DoesNotContain(hindi, g => g.Text == "्"); // the virama joined its consonant
        // The syllables drawn out of order carry what they read as.
        Assert.Equal("हि", hindi[0].Actual?.Text);
        Assert.Same(hindi[0].Actual, hindi[1].Actual);

        // Latin words in the same font are shaped as Latin (their kerning is added when they are drawn).
        var mixed = Shape(font, "Hi हि");
        Assert.Equal(new[] { "H", "i", " ", "ि", "ह" }, mixed.Select(g => g.Text));
        Assert.False(mixed[0].Kerned);
        Assert.True(mixed[3].Kerned);
    }

    [Fact]
    public void RephIsPlacedOnItsAnchor()
    {
        if (Nirmala() is not { } font || font.Positioning == null) return;
        var rki = Shape(font, "र्कि");
        // Drawn: i matra, ka, reph; the reph is moved onto the anchor above the ka (or above the matra's stem).
        Assert.Equal(new[] { "ि", "क", "र्" }, rki.Select(g => g.Text));
        var reph = rki[2];
        Assert.True(reph.Dx != 0 || reph.Dy != 0, "the reph is attached by the font's above-base mark positioning");
        Assert.Equal(0, reph.Advance ?? font.Advance(reph.Gid));
    }

    // Text in each script, with the constructs the reordering handles: pre-base matras, split
    // matras, reph, below-base and post-base forms, conjuncts, nukta. Left out: Telugu శ్రీ, where
    // Windows sorts the ii matra after the Ra, so a rule of Nirmala UI's keeps the Ra below the sha,
    // while this shaper, like HarfBuzz, sorts Telugu top matras before the Ra and draws its pre-base
    // form. Tested with Nirmala UI version 1.46.
    public static readonly string[] Samples =
    {
        "क्षि", "र्क", "हिन्दी", "र्कि", "प्रेम", "स्त्री", "कर्म", "द्ध", "क़िला", "श्री", "र्क्ष्य", "क्‍ष", "क्‌ष", "र्‍य", "क्ष्म", "ट्र",
        "বাংলা", "কৌ", "র্ক", "ক্ষি", "প্রে", "ৰ্ক",
        "ਪੰਜਾਬੀ", "ਕ੍ਰਿ", "ਗੁਰਮੁਖੀ",
        "ગુજરાતી", "ર્ક", "ક્ષિ",
        "ଓଡ଼ିଆ", "କ୍ରି", "କୌ", "ର୍କ",
        "தமிழ்", "கொ", "கௌ", "க்ஷ",
        "తెలుగు", "క్రి", "కై", "ర్క",
        "ಕನ್ನಡ", "ಕ್ರಿ", "ಕೊ", "ರ್ಕ",
        "മലയാളം", "ക്ര", "കൊ", "ർക",
        // Longer words.
        "नमस्ते", "संस्कृत", "विद्यालय", "प्रौद्योगिकी", "अर्थव्यवस्था", "उज्ज्वल", "द्वार", "ज्ञान", "कृष्ण", "आँख", "पढ़ाई", "र्त्स्न्य", "शृंगार", "र्द्र", "ि", "्",
        "ভাষা", "সংস্কৃতি", "বিজ্ঞান", "কার্য", "স্ত্রী", "র‍্যাব", "বৌদ্ধ", "দুর্গা",
        "ਸਿੱਖ", "ਪ੍ਰੇਮ", "ਸ੍ਵ", "ਖ਼ਾਲਸਾ",
        "શ્રી", "ક્ષત્રિય", "ધર્મ", "પ્રેમ",
        "ଶ୍ରୀ", "କ୍ଷ", "ଧର୍ମ", "ପ୍ରେମ", "କ୍ୟ",
        "ஸ்ரீ", "கொள்", "பெண்", "தொலைபேசி", "க்ஷ்மி",
        "క్ష్మి", "ధర్మం", "ప్రేమ", "స్త్రీ", "ర్క్య", "క్కి", "స్తె", "ప్తో", "ద్వే", "న్నీ", "శ్రే", "శ్రా", "శ్రు", "ష్ట్రీ", "క్రొ",
        "ಶ್ರೀ", "ಕ್ಷ್ಮಿ", "ಧರ್ಮ", "ಪ್ರೇಮ", "ಸ್ತ್ರೀ", "ಕೋ", "ರ್ಕ್ಯ", "ಕ್ಕಿ", "ಸ್ತೆ", "ದ್ವೇ", "ಶ್ರೇ", "ಕ್ರೊ", "ರ್ಕೆ", "ರ್ಕ್ಕಿ",
        "ക്ഷ", "സ്ത്രീ", "കേരളം", "ധർമ്മം", "പ്രേമം", "ക്യ", "ന്റെ", "കൈ", "ക്രൈ",
    };

    [Fact]
    public void GlyphsAndPositions_MatchWindowsShaping()
    {
        if (Nirmala() is not { } font || !OperatingSystem.IsWindows()) return;
        var mismatches = new List<string>();
        foreach (string text in Samples)
        {
            // At one pixel per font unit, Windows' positions are the font's own.
            if (Uniscribe.Shape(text, "Nirmala UI", font.UnitsPerEm) is not { } windows) continue;
            // Windows draws joiners as spaces of no width (the samples have no other spaces); they are not drawn here.
            var expected = windows.Where(g => g.Gid != font.GlyphFor(' ')).ToList();
            var ours = Shape(font, text);
            if (!ours.Select(g => g.Gid).SequenceEqual(expected.Select(g => g.Gid)))
            {
                mismatches.Add($"{text}: glyphs [{string.Join(' ', ours.Select(g => g.Gid))}], Windows [{string.Join(' ', expected.Select(g => g.Gid))}]");
                continue;
            }
            // Where each glyph is drawn from the start of the text, in 1/1000 em.
            double pen = 0, windowsPen = 0, k = 1000.0 / font.UnitsPerEm;
            for (int i = 0; i < ours.Count; i++)
            {
                double x = pen + ours[i].Dx, y = ours[i].Dy;
                double wx = (windowsPen + expected[i].Dx) * k, wy = expected[i].Dy * k;
                if (Math.Abs(x - wx) > 1.5 || Math.Abs(y - wy) > 1.5)
                    mismatches.Add($"{text}: glyph {i} ({ours[i].Gid}) at ({x:0.#}, {y:0.#}), Windows ({wx:0.#}, {wy:0.#})");
                pen += ours[i].Advance ?? font.Advance(ours[i].Gid);
                windowsPen += expected[i].Advance;
            }
        }
        foreach (var m in mismatches) _output.WriteLine(m);
        Assert.Empty(mismatches);
    }
    [Fact]
    public void V2ScriptFallsBackToTheFirstSpecificationsTag()
    {
        // A GSUB with only 'deva': its 'akhn' ligature (glyphs 5, 6 to 9) applies to dev2 text.
        static byte[] U16(params int[] values) => values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray();
        var scriptList = U16(1).Concat("deva"u8.ToArray()).Concat(U16(8)).Concat(U16(4, 0)).Concat(U16(0, 0xFFFF, 1, 0)).ToArray();
        var featureList = U16(1).Concat("akhn"u8.ToArray()).Concat(U16(8)).Concat(U16(0, 1, 0)).ToArray();
        var ligature = U16(1, 8, 1, 14).Concat(U16(1, 1, 5)).Concat(U16(1, 4)).Concat(U16(9, 2, 6)).ToArray();
        var lookup = U16(4, 0, 1, 8).Concat(ligature).ToArray();
        var lookupList = U16(1, 4).Concat(lookup).ToArray();
        int features = 10 + scriptList.Length, lookups = features + featureList.Length;
        var gsub = OpenTypeSubstitution.Read(new Dictionary<string, byte[]> { ["GSUB"] = U16(1, 0, 10, features, lookups).Concat(scriptList).Concat(featureList).Concat(lookupList).ToArray() })!;
        Assert.Equal("deva", gsub.ScriptTag("dev2"));
        Assert.True(gsub.WouldSubstitute("dev2", "akhn", 5, 6));
        Assert.False(gsub.WouldSubstitute("dev2", "akhn", 5));
        var run = new List<ShapingGlyph> { new() { Gid = 5, Text = "a" }, new() { Gid = 6, Text = "b" } };
        gsub.Apply(run, "dev2", new[] { "akhn" });
        Assert.Equal(9, Assert.Single(run).Gid);
        Assert.True(run[0].Ligated);
    }

    [Fact]
    public async Task AddedHindiText_ReadsBackAsTyped()
    {
        if (Nirmala() == null) return;
        const string text = "हिन्दी में र्क और क्षि";
        var b = new VectorPdfBuilder();
        b.AddPage("", "<< >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfAddText(1, new PdfPoint(40, 200), text, new PdfTextFormat("Nirmala UI", 24)) });
        Assert.Empty(result.Warnings);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(result.Bytes, null);
        string read = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
        _output.WriteLine(read);
        foreach (string word in text.Split(' ')) Assert.Contains(word, read);
    }

    /// <summary>Windows' shaping of a string in an installed font (Uniscribe, which shares DirectWrite's shaping engine).</summary>
    private static class Uniscribe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ScriptAnalysis
        {
            public ushort Flags;
            public ushort State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScriptItem
        {
            public int CharPos;
            public ScriptAnalysis Analysis;
        }

        [DllImport("usp10.dll", CharSet = CharSet.Unicode)]
        private static extern int ScriptItemize(string chars, int count, int maxItems, IntPtr control, IntPtr state, [Out] ScriptItem[] items, out int itemCount);

        [DllImport("usp10.dll", CharSet = CharSet.Unicode)]
        private static extern int ScriptShape(IntPtr hdc, ref IntPtr cache, string chars, int count, int maxGlyphs, ref ScriptAnalysis analysis,
            [Out] ushort[] glyphs, [Out] ushort[] clusters, [Out] ushort[] visualAttributes, out int glyphCount);

        [DllImport("usp10.dll")]
        private static extern int ScriptFreeCache(ref IntPtr cache);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
            uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("usp10.dll")]
        private static extern int ScriptPlace(IntPtr hdc, ref IntPtr cache, ushort[] glyphs, int count, ushort[] visualAttributes, ref ScriptAnalysis analysis,
            [Out] int[] advances, [Out] int[] offsets, out long abc);

        /// <summary>The glyphs, their advances and their offsets (x, and y up), in font units at a size of <paramref name="em"/> pixels to the em.</summary>
        public static List<(int Gid, int Advance, int Dx, int Dy)>? Shape(string text, string face, int em)
        {
            var items = new ScriptItem[text.Length + 2];
            if (ScriptItemize(text, text.Length, text.Length + 1, IntPtr.Zero, IntPtr.Zero, items, out int itemCount) != 0) return null;
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero), font = CreateFontW(-em, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, face);
            IntPtr old = SelectObject(dc, font), cache = IntPtr.Zero;
            try
            {
                var result = new List<(int, int, int, int)>();
                for (int i = 0; i < itemCount; i++)
                {
                    int from = items[i].CharPos, length = items[i + 1].CharPos - from;
                    int max = length * 3 + 16;
                    var glyphs = new ushort[max];
                    var clusters = new ushort[length];
                    var attributes = new ushort[max];
                    var analysis = items[i].Analysis;
                    if (ScriptShape(dc, ref cache, text.Substring(from, length), length, max, ref analysis, glyphs, clusters, attributes, out int count) != 0) return null;
                    var advances = new int[count];
                    var offsets = new int[count * 2];
                    if (ScriptPlace(dc, ref cache, glyphs, count, attributes, ref analysis, advances, offsets, out _) != 0) return null;
                    for (int k = 0; k < count; k++) result.Add((glyphs[k], advances[k], offsets[k * 2], offsets[k * 2 + 1]));
                }
                return result;
            }
            finally
            {
                ScriptFreeCache(ref cache);
                SelectObject(dc, old);
                DeleteObject(font);
                DeleteDC(dc);
            }
        }
    }
}
