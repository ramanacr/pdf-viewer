using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Objects;

namespace VectorPdf.Tool;

/// <summary>vectorpdf fontdebug &lt;file.pdf&gt; &lt;font name part&gt; &lt;code&gt;: how a code of a font maps to its program's glyph (for diagnosing font rules).</summary>
public static class FontDebug
{
    public static async Task<int> RunAsync(string[] args)
    {
        byte[] bytes = await File.ReadAllBytesAsync(args[1]);
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var r = doc.Resolver;
        int code = int.Parse(args[3]);
        Console.WriteLine($"{doc.XrefTable.Entries.Count} objects");
        var fonts = new System.Collections.Generic.List<(int, PdfDictionary)>();
        var seen = new System.Collections.Generic.HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Walk(PdfObject? o, int owner, int depth)
        {
            if (o == null || depth > 20) return;
            switch (o)
            {
                case PdfDictionary dd:
                    if (!seen.Add(dd)) return;
                    if (dd.GetName("BaseFont") != null && dd.GetName("Subtype") is "TrueType" or "Type1" or "Type0" or "MMType1") fonts.Add((owner, dd));
                    foreach (var v in dd.Entries.Values) if (v is not PdfIndirectRef) Walk(v, owner, depth + 1);
                    break;
                case PdfArray a: foreach (var v in a) if (v is not PdfIndirectRef) Walk(v, owner, depth + 1); break;
                case PdfStream st: Walk(st.Dictionary, owner, depth + 1); break;
            }
        }
        foreach (var (n, e) in doc.XrefTable.Entries) if (e.IsInUse) Walk(r.Resolve(n), n, 0);
        Console.WriteLine(string.Join(", ", fonts.Select(f => f.Item2.GetName("BaseFont"))));
        foreach (var (number, d) in fonts)
        {
            if (!(d.GetName("BaseFont") ?? "").Contains(args[2], StringComparison.Ordinal)) continue;
            var font = new PdfFontResolver(r).ResolveFont(d, "F");
            var prepared = FontProgramPreparer.Prepare(font);
            Console.WriteLine($"object {number}: {d.GetName("BaseFont")} {font.Subtype} symbolic={font.IsSymbolic} flags={font.Flags} program={font.EmbeddedProgramKind} prepared={prepared?.Format}");
            Console.WriteLine($"  encoding: {r.Resolve(d["Encoding"])}");
            Console.WriteLine($"  glyph name for {code}: {font.GetGlyphName(code)}; declared width {font.GetGlyphWidth(code)}");
            if (prepared != null)
            {
                int gid = prepared.GetGlyphId(code, code);
                var tt = PdfEngine.Vector.Editing.TrueTypeFontFile.TryLoad(prepared.Sfnt);
                Console.WriteLine($"  prepared gid {gid}; advance {(tt != null ? tt.Advance(gid) : double.NaN)}; glyphs {tt?.GlyphCount}");
                var orig = font.EmbeddedFontData != null ? PdfEngine.Vector.Editing.TrueTypeFontFile.TryLoad(font.EmbeddedFontData) : null;
                if (orig != null && gid >= 0) Console.WriteLine($"  original program advance of gid {gid}: {orig.Advance(gid)}");
                if (orig != null && gid >= 0)
                {
                    var patched = PdfEngine.Vector.Editing.TrueTypeFontFile.WithAdvances(font.EmbeddedFontData!, new System.Collections.Generic.Dictionary<int, int> { [gid] = 1000 });
                    var back = patched == null ? null : PdfEngine.Vector.Editing.TrueTypeFontFile.TryLoad(patched);
                    Console.WriteLine($"  patched: gid {gid} -> {back?.Advance(gid)}, gid 3 -> {back?.Advance(3)}, glyphs {back?.GlyphCount}, upm {back?.UnitsPerEm} vs {orig.UnitsPerEm}");
                }
            }
        }
        return 0;
    }
}
