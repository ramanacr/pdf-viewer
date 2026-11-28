using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PrintProduction;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// Output Preview (the Windows app's Print Production → Output Preview, after Acrobat Pro's): the page
/// separated by the engine into its process and spot plates (<see cref="PdfSeparationRenderer"/>, drawn by
/// <see cref="SkiaDisplayListRasterizer"/>), with plates turned on and off, overprint simulated or not, the
/// areas over a total ink limit highlighted, and a tap on the page reading each plate's ink and the total
/// there. It renders its own copy of the document (with the edits) on a worker thread; the pages behind
/// are not changed.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The longest side of the separated page, in pixels: sharp on a phone, and quick to separate plate by plate.</summary>
    private const int OutputPreviewPixels = 1000;

    /// <summary>Output Preview of the current page.</summary>
    private void ShowOutputPreview()
    {
        if (!NeedDocument() || CurrentBytes() is not { } bytes) return;
        var screen = new OutputPreviewScreen(this, bytes, Math.Clamp(_pages.CurrentPage, 1, _doc!.PageCount), _doc.PageCount);
        ShowToolOverlay(screen.Build(), screen.Close);
        screen.Render();
    }

    /// <summary>The Output Preview screen: its views, the separation of the page shown, and the work on its worker thread.</summary>
    private sealed class OutputPreviewScreen(MainActivity a, byte[] bytes, int firstPage, int pageCount)
    {
        /// <summary>One plate in the list: its channel in the separation, its colorant, and whether it is shown.</summary>
        private sealed class Plate(int channel, PdfColorant colorant)
        {
            public int Channel { get; } = channel;
            public PdfColorant Colorant { get; } = colorant;
            public bool Visible { get; set; } = true;
            public TextView? Ink { get; set; }
        }

        private PdfVectorDocument? _engine;
        private SkiaDisplayListRasterizer? _rasterizer;
        private PdfSeparation? _separation;
        private List<Plate> _plates = [];
        /// <summary>Plates the reader turned off, by name, kept from page to page.</summary>
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private int _page = firstPage;
        private bool _overprint = true, _highlight;
        private double _limit = 300;
        private CancellationTokenSource? _cts;
        /// <summary>The last render: renders run one after another, as they share the engine document.</summary>
        private Task _work = Task.CompletedTask;
        private int _composeVersion;
        private bool _closed;
        /// <summary>The pixel of the separation the reader tapped, or null.</summary>
        private (int X, int Y)? _probe;

        private TextView _title = null!, _readout = null!, _summary = null!, _limitText = null!, _prev = null!, _next = null!;
        private InkImageView _image = null!;
        private ProgressBar _progress = null!;
        private LinearLayout _plateList = null!;

        public View Build()
        {
            var root = new LinearLayout(a) { Orientation = global::Android.Widget.Orientation.Vertical, Clickable = true };
            root.SetBackgroundColor(a.ScreenColor);
            _prev = a.ToolButton("‹", "Previous page", (_, _) => Go(-1));
            _next = a.ToolButton("›", "Next page", (_, _) => Go(1));
            root.AddView(a.ToolScreenBar("Output preview", out _title, _prev, _next));

            var frame = new FrameLayout(a);
            _image = new InkImageView(a)
            {
                ContentDescription = "The page as it prints, with the plates that are on. Tap it to read the ink at that point.",
            };
            _image.SetScaleType(ImageView.ScaleType.FitCenter!);
            _image.SetPadding((int)a.Dp(8), (int)a.Dp(8), (int)a.Dp(8), (int)a.Dp(8));
            _image.Touch += OnImageTouch;
            frame.AddView(_image, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _progress = new ProgressBar(a) { Indeterminate = true };
            frame.AddView(_progress, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
            root.AddView(frame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 3f));

            _readout = Line(15);
            _readout.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
            root.AddView(_readout);
            _summary = Line(13);
            _summary.Alpha = 0.8f;
            root.AddView(_summary);

            var panel = new LinearLayout(a) { Orientation = global::Android.Widget.Orientation.Vertical };
            panel.SetPadding((int)a.Dp(12), 0, (int)a.Dp(12), (int)a.Dp(8));
            var overprint = Check("Simulate overprint", true);
            overprint.CheckedChange += (_, e) =>
            {
                _overprint = e.IsChecked;
                Render();
            };
            panel.AddView(overprint);

            var limitRow = new LinearLayout(a) { Orientation = global::Android.Widget.Orientation.Horizontal };
            limitRow.SetGravity(GravityFlags.CenterVertical);
            var highlight = Check("Highlight total ink over", false);
            highlight.ContentDescription = "Highlight in green where the total ink is over the limit";
            highlight.CheckedChange += (_, e) =>
            {
                _highlight = e.IsChecked;
                Compose();
            };
            limitRow.AddView(highlight, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var lower = a.ToolButton("−", "Lower the ink limit by 10 %", (_, _) => SetLimit(_limit - 10));
            _limitText = a.ToolButton("", "Ink limit; tap to type it", (_, _) => TypeLimit());
            var higher = a.ToolButton("+", "Raise the ink limit by 10 %", (_, _) => SetLimit(_limit + 10));
            foreach (var b in new[] { lower, _limitText, higher })
            {
                b.SetTextColor(a.AccentColor);
                limitRow.AddView(b);
            }
            panel.AddView(limitRow);

            var heading = Line(14);
            heading.Text = "Plates";
            heading.SetTextColor(a.AccentColor);
            heading.SetTypeface(null, TypefaceStyle.Bold);
            heading.SetPadding(0, (int)a.Dp(8), 0, 0);
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) heading.AccessibilityHeading = true;
            panel.AddView(heading);
            _plateList = new LinearLayout(a) { Orientation = global::Android.Widget.Orientation.Vertical };
            panel.AddView(_plateList);

            var scroll = new ScrollView(a);
            scroll.AddView(panel);
            root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 2f));
            ShowLimit();
            UpdateNavigation();
            ShowReadout();
            return root;
        }

        private TextView Line(float sp)
        {
            var text = new TextView(a);
            text.SetTextColor(a.TextColor);
            text.SetTextSize(global::Android.Util.ComplexUnitType.Sp, sp);
            text.SetPadding((int)a.Dp(16), (int)a.Dp(4), (int)a.Dp(16), (int)a.Dp(4));
            return text;
        }

        private CheckBox Check(string text, bool on)
        {
            var box = new CheckBox(a) { Text = text, Checked = on };
            box.SetTextColor(a.TextColor);
            box.SetMinHeight((int)a.Dp(48));
            return box;
        }

        // ------------------------------------------------------------------ separating the page

        /// <summary>Separates the current page on a worker thread (after the render before it, which is cancelled), then shows it.</summary>
        public void Render()
        {
            if (_closed) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            _progress.Visibility = ViewStates.Visible;
            _summary.Text = "Separating the page…";
            _work = RenderAsync(_work, cts, _page, _overprint);
        }

        private async Task RenderAsync(Task previous, CancellationTokenSource cts, int page, bool overprint)
        {
            await previous; // never throws: each render handles its own failure
            try
            {
                if (cts.IsCancellationRequested) return;
                var token = cts.Token;
                var (inventory, separation) = await Task.Run(async () =>
                {
                    _engine ??= a.OpenForTool(bytes);
                    _rasterizer ??= new SkiaDisplayListRasterizer();
                    var inv = await PdfColorantInventory.ReadAsync(_engine, [page], token).ConfigureAwait(false);
                    var (w, h) = PreviewSize(_engine, page);
                    var sep = await PdfSeparationRenderer.RenderAsync(_engine, page, w, h, _rasterizer,
                        new PdfSeparationOptions { Model = inv.CreateModel(), SimulateOverprint = overprint }, token).ConfigureAwait(false);
                    return (inv, sep);
                }, token);
                if (cts.IsCancellationRequested || _closed) return;
                _separation = separation;
                _plates = inventory.All.Select((c, i) => new Plate(i, c) { Visible = !_hidden.Contains(c.Name) }).ToList();
                FillPlates();
                await ComposeAsync();
            }
            catch (System.OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_closed || cts.IsCancellationRequested) return;
                _summary.Text = "This page could not be separated.";
                a.ShowFailure(ex);
            }
            finally
            {
                if (ReferenceEquals(_cts, cts) && !_closed) _progress.Visibility = ViewStates.Gone;
            }
        }

        /// <summary>The separation's size: the page's crop box (turned by its /Rotate) with its longest side <see cref="OutputPreviewPixels"/>.</summary>
        private static (int Width, int Height) PreviewSize(PdfVectorDocument engine, int page)
        {
            var node = engine.PageTree.Pages[page - 1];
            var crop = node.CropBox.IsEmpty ? new PdfRect(0, 0, node.PageSize.Width, node.PageSize.Height) : node.CropBox;
            bool sideways = ((node.RotationDegrees % 360) + 360) % 360 is 90 or 270;
            double w = sideways ? crop.Height : crop.Width, h = sideways ? crop.Width : crop.Height;
            double scale = OutputPreviewPixels / Math.Max(1, Math.Max(w, h));
            return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        }

        /// <summary>The plates shown, as a mask of the separation's channels.</summary>
        private ulong VisibleMask => _plates.Where(p => p.Visible).Aggregate(0UL, (m, p) => m | 1UL << p.Channel);

        private void Compose() => _ = ComposeAsync();

        /// <summary>Mixes the plates that are on into the picture on a worker thread (no new separation), with the ink highlight.</summary>
        private async Task ComposeAsync()
        {
            if (_separation is not { } s || _closed) return;
            int version = ++_composeVersion;
            ulong mask = VisibleMask;
            var spots = _plates.Where(p => !p.Colorant.IsProcess).Select(p => p.Colorant.Appearance).ToList();
            double limit = _limit;
            double? highlight = _highlight ? limit : null;
            Bitmap? bitmap;
            PdfInkCoverage coverage;
            try
            {
                (bitmap, coverage) = await Task.Run(() =>
                {
                    var bgra = s.CompositeToBgra(mask, spots, highlight);
                    var colors = new int[s.Width * s.Height];
                    Buffer.BlockCopy(bgra, 0, colors, 0, colors.Length * 4);
                    // On a little-endian device a BGRA pixel reads as an ARGB colour; made opaque.
                    for (int i = 0; i < colors.Length; i++) colors[i] |= unchecked((int)0xFF000000);
                    return (Bitmap.CreateBitmap(colors, s.Width, s.Height, Bitmap.Config.Argb8888!), s.Coverage(limit, mask));
                });
            }
            catch (OutOfMemoryException ex)
            {
                if (!_closed) a.ShowFailure(ex);
                return;
            }
            if (version != _composeVersion || _closed || !ReferenceEquals(s, _separation))
            {
                bitmap?.Recycle();
                return;
            }
            _image.SetImageBitmap(bitmap);
            string spotText = spots.Count switch { 0 => "no spot colours", 1 => "1 spot colour", var n => $"{n} spot colours" };
            string over = coverage.PixelsOver == 0
                ? $"nothing over {limit:0} %"
                : $"{coverage.FractionOver * 100:0.##} % of the page over {limit:0} %";
            _summary.Text = $"Page {_page}: {spotText}; total ink up to {coverage.MaxPercent:0} % ({over}). "
                            + (s.SimulatedOverprint ? "Overprinting simulated." : "Overprinting not simulated: every object knocks out what is under it.")
                            + (s.HasFallback ? " Parts of the page are drawn approximately." : "");
            ShowReadout();
        }

        // ------------------------------------------------------------------ plates and the ink limit

        private void FillPlates()
        {
            _plateList.RemoveAllViews();
            foreach (var plate in _plates)
            {
                var row = new LinearLayout(a) { Orientation = global::Android.Widget.Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                var c = plate.Colorant.Appearance;
                var swatch = new View(a) { ImportantForAccessibility = ImportantForAccessibility.No };
                swatch.SetBackgroundColor(Color.Rgb(ToByte(c.R), ToByte(c.G), ToByte(c.B)));
                row.AddView(swatch, new LinearLayout.LayoutParams((int)a.Dp(20), (int)a.Dp(20)) { RightMargin = (int)a.Dp(4) });
                string kind = plate.Colorant.IsProcess ? "process" : "spot";
                var box = Check(plate.Colorant.IsProcess ? plate.Colorant.Name : plate.Colorant.Name + " (spot)", plate.Visible);
                box.ContentDescription = $"{plate.Colorant.Name}, {kind} plate";
                var p = plate;
                box.CheckedChange += (_, e) =>
                {
                    p.Visible = e.IsChecked;
                    if (e.IsChecked) _hidden.Remove(p.Colorant.Name);
                    else _hidden.Add(p.Colorant.Name);
                    Compose();
                };
                row.AddView(box, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                plate.Ink = new TextView(a);
                plate.Ink.SetTextColor(a.TextColor);
                plate.Ink.SetPadding((int)a.Dp(8), 0, (int)a.Dp(8), 0);
                row.AddView(plate.Ink);
                _plateList.AddView(row);
            }
        }

        private static int ToByte(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);

        private void SetLimit(double limit)
        {
            _limit = Math.Clamp(Math.Round(limit), 1, 6400);
            ShowLimit();
            Compose();
        }

        private void ShowLimit()
        {
            _limitText.Text = $"{_limit:0} %";
            _limitText.ContentDescription = $"Ink limit {_limit:0} %; tap to type it";
        }

        private void TypeLimit() =>
            a.AskLine("Total ink limit", "The most ink all plates together may put on one point, in percent (often 240 to 340).",
                $"{_limit:0}", "Percent", InputTypes.ClassNumber, "Set", typed =>
                {
                    if (double.TryParse(typed.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                        && v is >= 1 and <= 6400)
                        SetLimit(v);
                    else
                        Toast.MakeText(a, "Type a number from 1 to 6400.", ToastLength.Short)?.Show();
                });

        // ------------------------------------------------------------------ pages and the ink readout

        private void Go(int delta)
        {
            int page = Math.Clamp(_page + delta, 1, pageCount);
            if (page == _page) return;
            _page = page;
            _probe = null;
            _image.Marker = null;
            UpdateNavigation();
            ShowReadout();
            Render();
        }

        private void UpdateNavigation()
        {
            _title.Text = $"Page {_page} of {pageCount}";
            _title.ContentDescription = $"Output preview, page {_page} of {pageCount}";
            _prev.Enabled = _page > 1;
            _prev.Alpha = _page > 1 ? 1f : 0.35f;
            _next.Enabled = _page < pageCount;
            _next.Alpha = _page < pageCount ? 1f : 0.35f;
        }

        private void OnImageTouch(object? sender, View.TouchEventArgs e)
        {
            e.Handled = true;
            if (e.Event is not { } ev || _separation is not { } s) return;
            if (ev.Action is not (MotionEventActions.Down or MotionEventActions.Move or MotionEventActions.Up)) return;
            using var inverse = new Matrix();
            if (_image.ImageMatrix is not { } matrix || !matrix.Invert(inverse)) return;
            float[] point = [ev.GetX() - _image.PaddingLeft, ev.GetY() - _image.PaddingTop];
            inverse.MapPoints(point);
            int x = (int)Math.Floor(point[0]), y = (int)Math.Floor(point[1]);
            if (x < 0 || y < 0 || x >= s.Width || y >= s.Height) return;
            _probe = (x, y);
            _image.Marker = (x + 0.5f, y + 0.5f);
            ShowReadout();
        }

        /// <summary>Each plate's ink and the total of the plates that are on, at the pixel tapped.</summary>
        private void ShowReadout()
        {
            if (_probe is not { } p || _separation is not { } s || p.X >= s.Width || p.Y >= s.Height)
            {
                _readout.Text = "Tap the page to read the ink at that point.";
                foreach (var plate in _plates) if (plate.Ink != null) plate.Ink.Text = "";
                return;
            }
            var parts = new List<string>(_plates.Count + 1);
            foreach (var plate in _plates)
            {
                string ink = $"{s.InkAt(plate.Channel, p.X, p.Y) * 100:0} %";
                if (plate.Ink != null) plate.Ink.Text = ink;
                parts.Add($"{Abbreviation(plate.Colorant.Name)} {ink}");
            }
            parts.Add($"Total {s.TotalInkAt(p.X, p.Y, VisibleMask):0} %");
            _readout.Text = string.Join("  ", parts);
        }

        private static string Abbreviation(string name) => name switch
        {
            "Cyan" => "C",
            "Magenta" => "M",
            "Yellow" => "Y",
            "Black" => "K",
            _ => name,
        };

        /// <summary>The screen closed: the render stops, then the engine copy and the renderer are released.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _cts?.Cancel();
            _ = ReleaseAsync(_work);
        }

        private async Task ReleaseAsync(Task work)
        {
            await work;
            _rasterizer?.Dispose();
            _rasterizer = null;
            _engine?.Dispose();
            _engine = null;
            _separation = null;
        }
    }

    /// <summary>The separated page, with a ring where the reader tapped (in the picture's pixels).</summary>
    private sealed class InkImageView : ImageView
    {
        private readonly Paint _dark = new() { AntiAlias = true, Color = Color.Black, StrokeWidth = 5 };
        private readonly Paint _light = new() { AntiAlias = true, Color = Color.White, StrokeWidth = 2.5f };
        private (float X, float Y)? _marker;

        public InkImageView(Context context) : base(context)
        {
            _dark.SetStyle(Paint.Style.Stroke);
            _light.SetStyle(Paint.Style.Stroke);
        }

        private InkImageView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

        /// <summary>The point tapped, in the picture's pixels, or null.</summary>
        public (float X, float Y)? Marker
        {
            get => _marker;
            set
            {
                _marker = value;
                Invalidate();
            }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (_marker is not { } m || ImageMatrix is not { } matrix) return;
            float[] point = [m.X, m.Y];
            matrix.MapPoints(point);
            float x = point[0] + PaddingLeft, y = point[1] + PaddingTop;
            float r = 12 * (Resources?.DisplayMetrics?.Density ?? 1f);
            canvas.DrawCircle(x, y, r, _dark);
            canvas.DrawCircle(x, y, r, _light);
        }
    }
}
