using System.Diagnostics;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Time a renderer spent in each stage since it was created, for the corpus profiler
/// (`vectorpdf perf`). Counts only; it never leaves the process and holds no document content.
/// Fonts and images are inclusive totals wherever they happen (analysis, preparation or drawing);
/// replay is the CPU time of submitting the drawing, flush the time Direct2D took to finish it on
/// EndDraw, readback the copy of the tile into the caller's buffer (waiting for the GPU included).
/// </summary>
internal sealed class RenderStages
{
    public long Analyze, Fonts, Images, Replay, Flush, Readback, ReadbackWait, Surfaces, Setup, Hop, Tiles;
    public int AnalysisCacheHits, ImagesDecoded, ImageDecodesSkipped, GlyphsRasterized, AtlasClears;

    /// <summary>Replay time by command: paths, clips, text, images, groups, other (shadings, patterns, state).</summary>
    public long[] Commands = new long[6];

    public static long Now => Stopwatch.GetTimestamp();

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public RenderStages Snapshot()
    {
        var copy = (RenderStages)MemberwiseClone();
        copy.Commands = (long[])Commands.Clone();
        return copy;
    }

    public void Reset()
    {
        Analyze = Fonts = Images = Replay = Flush = Readback = ReadbackWait = Surfaces = Setup = Hop = Tiles = 0;
        AnalysisCacheHits = ImagesDecoded = ImageDecodesSkipped = GlyphsRasterized = AtlasClears = 0;
        Array.Clear(Commands);
    }
}
