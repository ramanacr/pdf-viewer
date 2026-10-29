using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>A colorant the document paints with: a process ink or a spot colour, and the pages it is on.</summary>
/// <param name="Appearance">How the colorant shows at full tint on screen (its alternate colour), for previews.</param>
public sealed record PdfColorant(string Name, bool IsProcess, PdfColor Appearance, IReadOnlyList<int> Pages);

/// <summary>
/// The colorants of a document or of some pages, as the pages print: every colour space actually painted with,
/// in page content, form XObjects, tiling pattern cells, shadings, images (their own spaces and Indexed bases)
/// and printing annotations. A spot colour defined in the resources but never painted is not listed; soft mask
/// groups print no ink and are not looked at.
/// </summary>
public sealed class PdfColorantInventory
{
    private PdfColorantInventory(IReadOnlyList<PdfColorant> process, IReadOnlyList<PdfColorant> spots, IReadOnlyList<int> pages)
    {
        Process = process;
        Spots = spots;
        Pages = pages;
    }

    /// <summary>Cyan, Magenta, Yellow and Black, always listed (as Acrobat lists them); <see cref="PdfColorant.Pages"/> says where they are used.</summary>
    public IReadOnlyList<PdfColorant> Process { get; }

    /// <summary>Spot colorants (Separation and DeviceN names other than the process ones, /All and /None), in order of first use.</summary>
    public IReadOnlyList<PdfColorant> Spots { get; }

    /// <summary>Process then spot colorants: the plates of <see cref="CreateModel"/>, in its channel order.</summary>
    public IEnumerable<PdfColorant> All => Process.Concat(Spots);

    /// <summary>The pages that were read.</summary>
    public IReadOnlyList<int> Pages { get; }

    /// <summary>An ink model with a plate for every spot (up to <see cref="PdfInkModel.MaxSpots"/>).</summary>
    public PdfInkModel CreateModel() => new(Spots.Select(s => s.Name));

    /// <summary>Reads the colorants of <paramref name="pages"/> (1-based; all pages when null).</summary>
    public static async Task<PdfColorantInventory> ReadAsync(PdfVectorDocument document, IEnumerable<int>? pages = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var list = (pages ?? Enumerable.Range(1, document.PageCount)).Where(p => p >= 1 && p <= document.PageCount).Distinct().OrderBy(p => p).ToList();
        var collector = new Collector();
        foreach (int page in list)
        {
            collector.Page = page;
            try
            {
                await document.ObservePageAsync(page, collector, cancellationToken).ConfigureAwait(false);
            }
            catch (PdfVectorException failure) when (failure is not Parsing.PdfDataNotLoadedException)
            {
                // A page that cannot be read contributes what was seen before the failure.
            }
        }
        return collector.ToInventory(list);
    }

    /// <summary>Collects colorants from paint events (also used by preflight).</summary>
    internal sealed class Collector : IPdfPaintObserver
    {
        private readonly SortedSet<int>[] _process = [new(), new(), new(), new()];
        private readonly List<string> _spotOrder = new();
        private readonly Dictionary<string, (PdfColor Appearance, SortedSet<int> Pages)> _spots = new(StringComparer.Ordinal);

        public int Page { get; set; }

        public void OnPaint(PdfPaintEvent paint)
        {
            if (paint.ColorSpace is { } space && !paint.Invisible)
                Visit(space, 0);
        }

        /// <summary>Records one colour space (its spots and whether it uses process inks) for the current page.</summary>
        public void Visit(PdfColorSpace space, int depth)
        {
            if (depth > 8) return;
            // A space seen before on this page adds nothing new.
            if (depth == 0 && SeenOnPage(space)) return;
            switch (space)
            {
                case PdfColorSpace.IndexedColorSpace indexed:
                    Visit(indexed.BaseColorSpace, depth + 1);
                    break;
                case PdfColorSpace.SeparationColorSpace separation:
                    if (separation.ColorantName is "None" or "") break;
                    if (separation.ColorantName == "All") { AllProcess(); break; }
                    AddColorant(separation.ColorantName, () => SafeAppearance(separation, [1.0]));
                    break;
                case PdfColorSpace.DeviceNColorSpace deviceN:
                {
                    var names = deviceN.Colorants.ToArray();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (names[i] is "None" or "") continue;
                        int at = i;
                        AddColorant(names[i], () =>
                        {
                            var tints = new double[names.Length];
                            tints[at] = 1;
                            return SafeAppearance(deviceN, tints);
                        });
                    }
                    break;
                }
                case { IsPattern: true }:
                    if (space.PatternBaseColorSpace is { } patternBase) Visit(patternBase, depth + 1);
                    break;
                default:
                    if (space.Name == "DeviceGray" || space is PdfColorSpace.IccBasedColorSpace { NumberOfComponents: 1 } || space.Name == "CalGray")
                        _process[3].Add(Page);
                    else
                        AllProcess();
                    break;
            }
        }

        private readonly Dictionary<PdfColorSpace, int> _lastPage = new(ReferenceEqualityComparer.Instance);

        private bool SeenOnPage(PdfColorSpace space)
        {
            bool same = _lastPage.TryGetValue(space, out int last) && last == Page;
            _lastPage[space] = Page;
            return same;
        }

        private void AllProcess()
        {
            foreach (var set in _process) set.Add(Page);
        }

        private void AddColorant(string name, Func<PdfColor> appearance)
        {
            int process = PdfInkModel.ProcessColorants.ToList().IndexOf(name);
            if (process >= 0)
            {
                _process[process].Add(Page);
                return;
            }
            if (!_spots.TryGetValue(name, out var entry))
            {
                _spots[name] = entry = (appearance(), new SortedSet<int>());
                _spotOrder.Add(name);
            }
            entry.Pages.Add(Page);
        }

        private static PdfColor SafeAppearance(PdfColorSpace space, double[] components)
        {
            try
            {
                var c = space.ToRgbColor(components);
                return new PdfColor(c.R, c.G, c.B, 1);
            }
            catch (PdfVectorException failure) when (failure is not Parsing.PdfDataNotLoadedException)
            {
                return PdfColor.FromGray(0.5f);
            }
        }

        public IReadOnlyList<string> SpotNames => _spotOrder;

        public PdfColor AppearanceOf(string spot) => _spots.TryGetValue(spot, out var e) ? e.Appearance : PdfColor.FromGray(0.5f);

        public PdfColorantInventory ToInventory(IReadOnlyList<int> pages)
        {
            var process = new List<PdfColorant>(4);
            for (int i = 0; i < 4; i++)
            {
                var unit = new double[4];
                unit[i] = 1;
                process.Add(new PdfColorant(PdfInkModel.ProcessColorants[i], true, PdfInkModel.ShowCmyk(unit[0], unit[1], unit[2], unit[3]), _process[i].ToList()));
            }
            var spots = _spotOrder.Select(n => new PdfColorant(n, false, _spots[n].Appearance, _spots[n].Pages.ToList())).ToList();
            return new PdfColorantInventory(process, spots, pages);
        }
    }
}
