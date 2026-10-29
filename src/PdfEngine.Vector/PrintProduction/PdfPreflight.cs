using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>
/// A preflight profile: which checks run and how severe each is. The built-in profiles are
/// <see cref="DigitalPrinting"/> and <see cref="PdfX1aReadiness"/>.
/// </summary>
public sealed class PdfPreflightProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>What the profile does not do, printed with every report.</summary>
    public string Disclaimer { get; init; } = "Preflight checks the file; it changes nothing.";

    public PdfPreflightSeverity? FontsNotEmbedded { get; init; }
    /// <summary>Images below this resolution (ppi) are errors.</summary>
    public double? ImageErrorPpi { get; init; }
    /// <summary>Images below this resolution (ppi) are warnings.</summary>
    public double? ImageWarningPpi { get; init; }
    public PdfPreflightSeverity? RgbContent { get; init; }
    /// <summary>Device-independent colour (Lab, CalRGB, CalGray, ICCBased) other than RGB, which <see cref="RgbContent"/> covers.</summary>
    public PdfPreflightSeverity? DeviceIndependentColor { get; init; }
    public double? InkLimitPercent { get; init; }
    public double? HairlinePoints { get; init; }
    public PdfPreflightSeverity? Transparency { get; init; }
    public bool ListSpotColors { get; init; }
    /// <summary>Pages should have a TrimBox (warning) and a BleedBox (information).</summary>
    public bool TrimAndBleedBoxes { get; init; }
    /// <summary>PDF/X: every page needs a TrimBox or an ArtBox (error).</summary>
    public bool TrimOrArtBoxRequired { get; init; }
    /// <summary>PDF/X: the catalog needs a GTS_PDFX output intent (error).</summary>
    public bool OutputIntentRequired { get; init; }
    /// <summary>PDF/X: the file must not be encrypted (error).</summary>
    public bool EncryptionForbidden { get; init; }

    public static readonly PdfPreflightProfile DigitalPrinting = new()
    {
        Id = "digital-printing-basic",
        Name = "Digital printing (basic)",
        Description = "Fonts embedded, image resolution (under 150 ppi an error, under 300 ppi a warning), RGB content, total ink over 300 %, "
                      + "hairlines thinner than 0.25 pt, transparency, spot colours, and TrimBox / BleedBox.",
        FontsNotEmbedded = PdfPreflightSeverity.Error,
        ImageErrorPpi = 150,
        ImageWarningPpi = 300,
        RgbContent = PdfPreflightSeverity.Warning,
        InkLimitPercent = 300,
        HairlinePoints = 0.25,
        Transparency = PdfPreflightSeverity.Info,
        ListSpotColors = true,
        TrimAndBleedBoxes = true,
    };

    public static readonly PdfPreflightProfile PdfX1aReadiness = new()
    {
        Id = "pdfx-1a-readiness",
        Name = "PDF/X-1a readiness",
        Description = "What PDF/X-1a (ISO 15930-1/-4) forbids or requires: no RGB or device-independent colour, no transparency, "
                      + "fonts embedded, a GTS_PDFX output intent, a TrimBox or ArtBox on every page, no encryption.",
        Disclaimer = "Readiness checks only: this is not a PDF/X validator, and nothing is converted. A file that passes still needs "
                     + "a PDF/X validator and the version keys (GTS_PDFXVersion) before it can claim PDF/X-1a.",
        FontsNotEmbedded = PdfPreflightSeverity.Error,
        RgbContent = PdfPreflightSeverity.Error,
        DeviceIndependentColor = PdfPreflightSeverity.Error,
        Transparency = PdfPreflightSeverity.Error,
        ListSpotColors = true,
        TrimOrArtBoxRequired = true,
        OutputIntentRequired = true,
        EncryptionForbidden = true,
    };

    public static IReadOnlyList<PdfPreflightProfile> BuiltIn { get; } = [DigitalPrinting, PdfX1aReadiness];

    public override string ToString() => Name;
}

/// <summary>How preflight runs.</summary>
public sealed class PdfPreflightOptions
{
    /// <summary>
    /// Draws plates to measure total ink per pixel (overprint simulated). Without one, total ink is measured per
    /// painted colour (fills, strokes, text): images and objects overprinting each other are then not measured.
    /// </summary>
    public IPdfDisplayListRasterizer? Rasterizer { get; init; }

    /// <summary>Resolution of the ink coverage plates.</summary>
    public double InkCoverageDpi { get; init; } = 36;

    /// <summary>Check at most this many pages (the first ones).</summary>
    public int MaxPages { get; init; } = int.MaxValue;

    /// <summary>At most this many findings per rule and page; the rest are summarised in one.</summary>
    public int MaxFindingsPerRulePerPage { get; init; } = 25;
}

/// <summary>
/// Print preflight (Acrobat Pro's Preflight, for the checks of <see cref="PdfPreflightProfile"/>): pages are
/// interpreted as they print and every painting operation is examined, so content inside form XObjects,
/// patterns and annotations is found with its page and position. Reports carry no page text.
/// </summary>
public static class PdfPreflight
{
    public static async Task<PdfPreflightReport> RunAsync(PdfVectorDocument document, PdfPreflightProfile profile,
        PdfPreflightOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(profile);
        options ??= new PdfPreflightOptions();
        var run = new Run(document, profile, options);
        await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new PdfPreflightReport
        {
            Profile = profile,
            Findings = run.Findings,
            PagesChecked = run.PagesChecked,
            PageCount = document.PageCount,
            InkCoverageRasterized = profile.InkLimitPercent != null && options.Rasterizer != null,
            InkCoverageDpi = options.InkCoverageDpi,
        };
    }

    private sealed class Run : IPdfPaintObserver
    {
        private readonly PdfVectorDocument _doc;
        private readonly PdfPreflightProfile _profile;
        private readonly PdfPreflightOptions _options;
        private readonly PdfColorantInventory.Collector _colorants = new();
        private readonly List<PdfPreflightFinding> _findings = new();
        private readonly Dictionary<(string Rule, int Page), int> _perPage = new();
        private readonly Dictionary<PdfFont, (int FirstPage, bool Visible, SortedSet<int> Pages)> _fonts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<(int Page, int Object)> _images = new();
        private readonly List<(int Page, PdfColorSpace Space, double[] Components, PdfRect Bounds)> _inkObjects = new();
        private int _page;

        public Run(PdfVectorDocument doc, PdfPreflightProfile profile, PdfPreflightOptions options)
        {
            _doc = doc;
            _profile = profile;
            _options = options;
        }

        public List<PdfPreflightFinding> Findings => _findings;
        public int PagesChecked { get; private set; }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            if (_profile.EncryptionForbidden && _doc.IsEncrypted)
                Add("PP-8.2", PdfPreflightSeverity.Error, "The file is encrypted; PDF/X does not allow encryption.");
            if (_profile.OutputIntentRequired && !HasPdfXOutputIntent())
                Add("PP-8.1", PdfPreflightSeverity.Error, "The document has no output intent of subtype GTS_PDFX (the printing condition it was made for).");

            int pages = Math.Min(_doc.PageCount, Math.Max(0, _options.MaxPages));
            var inkPages = new List<int>();
            for (int page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();
                _page = page;
                _colorants.Page = page;
                try
                {
                    await _doc.ObservePageAsync(page, this, ct).ConfigureAwait(false);
                    inkPages.Add(page);
                }
                catch (PdfVectorException ex) when (ex is not Parsing.PdfDataNotLoadedException)
                {
                    Add("PP-0.1", PdfPreflightSeverity.Error, $"The page could not be read ({ex.GetType().Name}); it was not checked.", page);
                }
                CheckPageBoxes(page);
                CheckPageGroup(page);
                PagesChecked++;
            }

            CheckFonts();
            if (_profile.ListSpotColors)
            {
                foreach (var colorant in _colorants.ToInventory([]).Spots)
                {
                    string spot = colorant.Name;
                    var pagesOf = colorant.Pages;
                    Add("PP-3.3", PdfPreflightSeverity.Info,
                        $"Spot colour \"{spot}\" on {Plural(pagesOf.Count, "page")} ({string.Join(", ", pagesOf.Take(10))}{(pagesOf.Count > 10 ? ", ..." : string.Empty)}).",
                        pagesOf.Count > 0 ? pagesOf[0] : null);
                }
            }
            if (_profile.InkLimitPercent is double limit)
                await CheckInkAsync(limit, inkPages, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ events

        public void OnPaint(PdfPaintEvent paint)
        {
            _colorants.OnPaint(paint);
            if (paint.Font != null && paint.Kind is PdfPaintKind.Text)
                NoteFont(paint.Font, !paint.Invisible);
            if (paint.Invisible)
                return;

            if (paint.Kind is PdfPaintKind.Image)
                CheckImage(paint);

            if (paint.ColorSpace is { } space && paint.Kind != PdfPaintKind.TransparencyGroup)
                CheckColour(paint, space);

            if (_profile.HairlinePoints is double hair && paint.Kind == PdfPaintKind.Stroke && paint.LineWidth < hair)
            {
                AddOnPage("PP-5.1", PdfPreflightSeverity.Warning,
                    paint.LineWidth <= 0
                        ? "A zero-width line (the thinnest the device can draw) is thinner than " + Points(hair) + "."
                        : $"A line {Points(paint.LineWidth)} wide is thinner than {Points(hair)}.",
                    paint.Bounds, measure: paint.LineWidth, unit: "pt");
            }

            if (_profile.Transparency is { } transparency)
                CheckTransparency(paint, transparency);

            if (_profile.InkLimitPercent != null && _options.Rasterizer == null && paint.Components is { } comps && paint.ColorSpace is { } inkSpace
                && paint.Kind is PdfPaintKind.Fill or PdfPaintKind.Stroke or PdfPaintKind.Text or PdfPaintKind.ImageMask && _inkObjects.Count < 20000)
            {
                _inkObjects.Add((_page, inkSpace, comps, paint.Bounds));
            }
        }

        private void NoteFont(PdfFont font, bool visible)
        {
            if (font.IsType3) return; // glyphs are content streams: always in the file
            if (!_fonts.TryGetValue(font, out var entry))
                _fonts[font] = entry = (_page, visible, new SortedSet<int>());
            else if (visible && !entry.Visible)
                _fonts[font] = entry = (entry.FirstPage, true, entry.Pages);
            entry.Pages.Add(_page);
        }

        private void CheckImage(PdfPaintEvent paint)
        {
            if (_profile.ImageErrorPpi == null && _profile.ImageWarningPpi == null) return;
            if (double.IsInfinity(paint.ImagePpi) || paint.ImagePpi <= 0) return;
            if (paint.ObjectNumber >= 0 && !_images.Add((_page, paint.ObjectNumber))) return; // the same image again on this page
            string size = $"{paint.ImageWidth} x {paint.ImageHeight} pixels";
            if (_profile.ImageErrorPpi is double error && paint.ImagePpi < error)
                AddOnPage("PP-2.1", PdfPreflightSeverity.Error, $"An image ({size}) is placed at {paint.ImagePpi:0} ppi, under {error:0} ppi.", paint.Bounds,
                    paint.ObjectNumber, paint.ImagePpi, "ppi");
            else if (_profile.ImageWarningPpi is double warning && paint.ImagePpi < warning)
                AddOnPage("PP-2.2", PdfPreflightSeverity.Warning, $"An image ({size}) is placed at {paint.ImagePpi:0} ppi, under {warning:0} ppi.", paint.Bounds,
                    paint.ObjectNumber, paint.ImagePpi, "ppi");
        }

        private void CheckColour(PdfPaintEvent paint, PdfColorSpace space)
        {
            var kind = Classify(space, 0);
            string what = paint.Kind switch
            {
                PdfPaintKind.Fill => "a fill",
                PdfPaintKind.Stroke => "a stroke",
                PdfPaintKind.Text => "text",
                PdfPaintKind.Image => "an image",
                PdfPaintKind.ImageMask => "an image mask",
                PdfPaintKind.Shading => "a shading",
                _ => "an object",
            };
            int obj = paint.Kind == PdfPaintKind.Image ? paint.ObjectNumber : -1;
            if (kind.Rgb && _profile.RgbContent is { } rgb)
                AddOnPage("PP-3.1", rgb, $"RGB colour ({kind.Name}) in {what}.", paint.Bounds, obj);
            else if (kind.DeviceIndependent && _profile.DeviceIndependentColor is { } cie)
                AddOnPage("PP-3.2", cie, $"Device-independent colour ({kind.Name}) in {what}.", paint.Bounds, obj);
        }

        /// <summary>Whether a painted space is RGB or another device-independent space (Indexed by its base, patterns by theirs).</summary>
        private static (bool Rgb, bool DeviceIndependent, string Name) Classify(PdfColorSpace space, int depth)
        {
            if (depth > 8) return (false, false, space.Name);
            switch (space)
            {
                case PdfColorSpace.IndexedColorSpace indexed:
                    var inner = Classify(indexed.BaseColorSpace, depth + 1);
                    return inner with { Name = "Indexed " + inner.Name };
                case { IsPattern: true }:
                    return space.PatternBaseColorSpace is { } b ? Classify(b, depth + 1) : (false, false, "Pattern");
                case PdfColorSpace.IccBasedColorSpace icc:
                    return icc.NumberOfComponents == 3 ? (true, true, "ICCBased RGB") : (false, true, $"ICCBased, {icc.NumberOfComponents} components");
                case PdfColorSpace.SeparationColorSpace or PdfColorSpace.DeviceNColorSpace:
                    return (false, false, space.Name);
            }
            return space.Name switch
            {
                "DeviceRGB" => (true, false, "DeviceRGB"),
                "CalRGB" => (true, true, "CalRGB"),
                "Lab" or "CalGray" => (false, true, space.Name),
                _ => (false, false, space.Name),
            };
        }

        private void CheckTransparency(PdfPaintEvent paint, PdfPreflightSeverity severity)
        {
            var reasons = new List<string>(3);
            if (paint.Kind == PdfPaintKind.TransparencyGroup)
            {
                // A transparency group by itself changes nothing; its alpha, blend or mask do.
                if (paint.Alpha < 1 || paint.Blend != PdfBlendMode.Normal || paint.SoftMask)
                    reasons.Add("a transparency group composited with transparency");
                else
                {
                    AddOnPage("PP-6.2", PdfPreflightSeverity.Info, "A form XObject is a transparency group.", paint.Bounds);
                    return;
                }
            }
            else
            {
                if (paint.Alpha < 1) reasons.Add($"opacity {paint.Alpha * 100:0} %");
                if (paint.Blend != PdfBlendMode.Normal) reasons.Add($"blend mode {paint.Blend}");
                if (paint.SoftMask) reasons.Add("a soft mask");
                if (paint.ImageSoftMask) reasons.Add("an image soft mask");
            }
            if (reasons.Count > 0)
                AddOnPage("PP-6.1", severity, $"Transparency: {string.Join(", ", reasons)}.", paint.Bounds,
                    paint.Kind == PdfPaintKind.Image ? paint.ObjectNumber : -1);
        }

        // ------------------------------------------------------------------ document and page checks

        private void CheckFonts()
        {
            if (_profile.FontsNotEmbedded is not { } severity) return;
            foreach (var (font, (first, visible, pages)) in _fonts)
            {
                if (font.EmbeddedProgramKind != PdfFontProgramKind.None) continue;
                string name = string.IsNullOrEmpty(font.BaseFont) ? "(unnamed)" : font.BaseFont;
                if (visible)
                    Add("PP-1.1", severity, $"Font \"{name}\" ({font.Subtype}) is not embedded; used on {Plural(pages.Count, "page")}.", first);
                else
                    Add("PP-1.2", PdfPreflightSeverity.Info, $"Font \"{name}\" ({font.Subtype}) is not embedded; it is only used for invisible text.", first);
            }
        }

        private void CheckPageBoxes(int page)
        {
            var dict = _doc.PageTree.Pages[page - 1].Dictionary;
            bool trim = _doc.Resolver.Resolve(dict["TrimBox"]) is PdfArray;
            bool bleed = _doc.Resolver.Resolve(dict["BleedBox"]) is PdfArray;
            bool art = _doc.Resolver.Resolve(dict["ArtBox"]) is PdfArray;
            if (_profile.TrimAndBleedBoxes)
            {
                if (!trim) AddSummarised("PP-7.1", PdfPreflightSeverity.Warning, "page(s) have no TrimBox (the finished size).", page);
                if (!bleed) AddSummarised("PP-7.2", PdfPreflightSeverity.Info, "page(s) have no BleedBox.", page);
            }
            if (_profile.TrimOrArtBoxRequired && !trim && !art)
                AddSummarised("PP-7.3", PdfPreflightSeverity.Error, "page(s) have neither a TrimBox nor an ArtBox; PDF/X requires one.", page);
        }

        /// <summary>One finding per rule for every page it applies to, counted ("3 page(s) have ..."), at the first page.</summary>
        private void AddSummarised(string rule, PdfPreflightSeverity severity, string tail, int page)
        {
            int at = _findings.FindIndex(f => f.RuleId == rule);
            if (at < 0)
            {
                _findings.Add(new PdfPreflightFinding(rule, severity, "1 " + tail) { Page = page });
                return;
            }
            var f = _findings[at];
            _findings[at] = f with { Count = f.Count + 1, Message = $"{f.Count + 1} {tail}" };
        }

        private void CheckPageGroup(int page)
        {
            if (_profile.Transparency == null) return;
            var dict = _doc.PageTree.Pages[page - 1].Dictionary;
            if (_doc.Resolver.Resolve(dict["Group"]) is PdfDictionary group && group.GetName("S") == "Transparency")
                Add("PP-6.2", PdfPreflightSeverity.Info, "The page has a transparency group.", page);
        }

        private bool HasPdfXOutputIntent()
        {
            if (_doc.Resolver.Resolve(_doc.Catalog["OutputIntents"]) is not PdfArray intents) return false;
            foreach (var item in intents)
                if (_doc.Resolver.Resolve(item) is PdfDictionary intent && intent.GetName("S") == "GTS_PDFX")
                    return true;
            return false;
        }

        private async Task CheckInkAsync(double limit, List<int> pages, CancellationToken ct)
        {
            var model = new PdfInkModel(_colorants.SpotNames);
            if (_options.Rasterizer is { } rasterizer)
            {
                foreach (int page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    var node = _doc.PageTree.Pages[page - 1];
                    var crop = node.CropBox.IsEmpty ? new PdfRect(0, 0, node.PageSize.Width, node.PageSize.Height) : node.CropBox;
                    bool sideways = ((node.RotationDegrees % 360) + 360) % 360 is 90 or 270;
                    double scale = _options.InkCoverageDpi / 72.0;
                    int w = Math.Clamp((int)Math.Round((sideways ? crop.Height : crop.Width) * scale), 1, 4000);
                    int h = Math.Clamp((int)Math.Round((sideways ? crop.Width : crop.Height) * scale), 1, 4000);
                    PdfSeparation separation;
                    try
                    {
                        separation = await PdfSeparationRenderer.RenderAsync(_doc, page, w, h, rasterizer,
                            new PdfSeparationOptions { Model = model, SimulateOverprint = true }, ct).ConfigureAwait(false);
                    }
                    catch (PdfVectorException failure) when (failure is not Parsing.PdfDataNotLoadedException)
                    {
                        continue;
                    }
                    var coverage = separation.Coverage(limit);
                    if (coverage.PixelsOver == 0) continue;
                    var bounds = coverage.OverBounds is { } px ? PdfSeparationRenderer.PixelsToPage(px, crop, node.RotationDegrees, w, h) : (PdfRect?)null;
                    Add("PP-4.1", PdfPreflightSeverity.Warning,
                        $"Total ink reaches {coverage.MaxPercent:0} %, over the {limit:0} % limit, on {coverage.FractionOver * 100:0.##} % of the page.",
                        page, bounds, measure: coverage.MaxPercent, unit: "%");
                }
                return;
            }

            // Per painted colour.
            var inks = new double[model.ChannelCount];
            var counts = new Dictionary<int, int>();
            foreach (var (page, space, comps, bounds) in _inkObjects)
            {
                model.Evaluate(space, comps, inks);
                double total = inks.Sum() * 100;
                if (total <= limit + 0.05) continue;
                counts.TryGetValue(page, out int n);
                counts[page] = n + 1;
                if (n >= _options.MaxFindingsPerRulePerPage) continue;
                Add("PP-4.1", PdfPreflightSeverity.Warning, $"A colour with {total:0} % total ink, over the {limit:0} % limit.", page, bounds,
                    measure: total, unit: "%");
            }
        }

        // ------------------------------------------------------------------ findings

        private void AddOnPage(string rule, PdfPreflightSeverity severity, string message, PdfRect bounds, int obj = -1, double? measure = null, string? unit = null)
        {
            var key = (rule, _page);
            _perPage.TryGetValue(key, out int n);
            _perPage[key] = n + 1;
            if (n < _options.MaxFindingsPerRulePerPage)
            {
                Add(rule, severity, message, _page, bounds, obj, measure, unit);
                return;
            }
            // Past the cap: one more finding on the page counts the rest.
            int at = _findings.FindLastIndex(f => f.RuleId == rule && f.Page == _page && f.Message.StartsWith("And ", StringComparison.Ordinal));
            if (at < 0)
                _findings.Add(new PdfPreflightFinding(rule, severity, "And 1 more like this on the page.") { Page = _page });
            else
            {
                var f = _findings[at];
                _findings[at] = f with { Count = f.Count + 1, Message = $"And {f.Count + 1} more like this on the page." };
            }
        }

        private void Add(string rule, PdfPreflightSeverity severity, string message, int? page = null, PdfRect? bounds = null, int obj = -1,
            double? measure = null, string? unit = null)
        {
            PdfRect? normalized = null;
            if (bounds is { } b && page is { } p && p >= 1 && p <= _doc.PageCount)
            {
                var crop = _doc.PageTree.Pages[p - 1].CropBox;
                if (crop.Width > 0 && crop.Height > 0)
                {
                    double x = Math.Clamp((b.X - crop.X) / crop.Width, 0, 1), y = Math.Clamp(1 - (b.Y + b.Height - crop.Y) / crop.Height, 0, 1);
                    double w = Math.Max(0.005, Math.Min(b.Width / crop.Width, 1 - x)), h = Math.Max(0.005, Math.Min(b.Height / crop.Height, 1 - y));
                    normalized = new PdfRect(x, y, w, h);
                }
            }
            _findings.Add(new PdfPreflightFinding(rule, severity, message)
            {
                Page = page,
                Bounds = bounds,
                NormalizedBounds = normalized,
                ObjectNumber = obj >= 0 ? obj : null,
                Measure = measure,
                Unit = unit,
            });
        }

        private static string Points(double v) => v.ToString(v < 1 ? "0.###" : "0.##", CultureInfo.InvariantCulture) + " pt";

        private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }
}
