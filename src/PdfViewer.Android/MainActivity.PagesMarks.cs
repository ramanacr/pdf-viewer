using System.Globalization;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Vector.Editing;
using PdfViewer.Web.Engine;

namespace PdfViewer.Android;

/// <summary>
/// Headers, footers and watermarks (Acrobat's Edit PDF page marks, through the engine's
/// <see cref="PdfPageMarks"/>): headers and footers with page numbers, dates and the file name,
/// Bates numbers, a text watermark, and removing the marks a document has (ours or Acrobat's).
/// Each is saved as a new PDF; the open document is not changed. The dialogs start from the
/// settings the document's marks were made with, when this app or the desktop viewer made them.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>Headers, footers and watermarks: reads the marks the document has, then asks what to do.</summary>
    private async void ShowPageMarks()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (doc.CertificationLevel is 1 or 2)
        {
            ShowFailure(new UserMessageException("This document is certified, and its certification does not allow changing its pages."));
            return;
        }
        if (doc.IsSigned && doc.EditRestriction is { } restriction)
        {
            ShowFailure(new UserMessageException(restriction));
            return;
        }
        if (CurrentBytes() is not { } bytes) return;
        var info = await RunBusyAsync("Reading the pages…", _ =>
        {
            using var engine = OpenForTool(bytes);
            if (!PermissionsOf(engine).CanModify)
                throw new UserMessageException("This document's security settings do not allow changing its pages.");
            return PdfPageMarks.Read(engine);
        });
        if (info == null || !ReferenceEquals(doc, _doc)) return;
        var choices = new List<(string Title, Action Run)>
        {
            (info.Has(PdfMarkKind.HeaderFooter) ? "Header and footer (change)" : "Header and footer", () => ShowHeaderFooterDialog(doc, bytes, info)),
            (info.Has(PdfMarkKind.Watermark) ? "Watermark (change)" : "Watermark", () => ShowWatermarkDialog(doc, bytes, info)),
            (info.Has(PdfMarkKind.Bates) ? "Bates numbering (change)" : "Bates numbering", () => ShowBatesDialog(doc, bytes, info)),
        };
        if (info.Marks.Count > 0) choices.Add(("Remove marks…", () => ShowRemoveMarksDialog(doc, bytes, info)));
        new AlertDialog.Builder(this)
            .SetTitle("Headers, footers and watermarks")!
            .SetItems(choices.Select(c => c.Title).ToArray(), (_, e) => choices[e.Which].Run())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>The note every page mark dialog starts with: the result is a new file, and what a signature will show.</summary>
    private string MarksNote(ViewerDocument doc) =>
        "Saved as a new PDF; this document is not changed." + (doc.IsSigned ? " The new PDF's signatures will show that it was changed after signing." : "");

    /// <summary>Margins are typed in millimetres; the engine takes points.</summary>
    private const double PointsPerMm = 72 / 25.4;

    /// <summary>Header and footer: text in six places with page number, page count, date and file name codes.</summary>
    private void ShowHeaderFooterDialog(ViewerDocument doc, byte[] bytes, PdfPageMarksInfo info)
    {
        var old = info.HeaderFooter;
        bool has = info.Has(PdfMarkKind.HeaderFooter);
        var form = FormColumn();
        FormNote(form, MarksNote(doc));
        FormNote(form, "Codes: <<1>> page number, <<n>> number of pages, <<Date>> today's date, <<File>> file name. The buttons below put one where you type.");
        EditText? focused = null;
        var codes = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        foreach (var (text, code, description) in new[]
                 {
                     ("Page", "<<1>>", "Insert the page number code"),
                     ("Pages", "<<n>>", "Insert the number of pages code"),
                     ("Date", "<<Date>>", "Insert today's date code"),
                     ("File", "<<File>>", "Insert the file name code"),
                 })
        {
            var button = ToolButton(text, description, (_, _) =>
            {
                if (focused == null) return;
                int start = Math.Max(0, Math.Min(focused.SelectionStart, focused.SelectionEnd));
                int end = Math.Max(0, Math.Max(focused.SelectionStart, focused.SelectionEnd));
                focused.EditableText?.Replace(start, end, code);
            });
            button.SetTextColor(AccentColor);
            codes.AddView(button);
        }
        var scrollCodes = new HorizontalScrollView(this);
        scrollCodes.AddView(codes);
        form.AddView(scrollCodes);
        var type = InputTypes.ClassText;
        var topLeft = FormField(form, "Header, left", old?.TopLeft ?? "", type);
        var topCenter = FormField(form, "Header, centre", old?.TopCenter ?? "", type);
        var topRight = FormField(form, "Header, right", old?.TopRight ?? "", type);
        var bottomLeft = FormField(form, "Footer, left", old?.BottomLeft ?? "", type);
        var bottomCenter = FormField(form, "Footer, centre", old?.BottomCenter ?? "Page <<1>> of <<n>>", type);
        var bottomRight = FormField(form, "Footer, right", old?.BottomRight ?? "", type);
        foreach (var field in new[] { topLeft, topCenter, topRight, bottomLeft, bottomCenter, bottomRight })
            field.FocusChange += (_, e) => { if (e.HasFocus) focused = field; };
        var number = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal;
        var size = FormField(form, "Font size (points)", Num(old?.Format.Size ?? 10), number);
        var vertical = FormField(form, "Distance from the top and bottom edges (mm)", Num((old?.TopMargin ?? 36) / PointsPerMm, 0), number);
        var horizontal = FormField(form, "Distance from the left and right edges (mm)", Num((old?.LeftMargin ?? 72) / PointsPerMm, 0), number);
        var first = FormField(form, "Number of the first page", Num(old?.StartPageNumber ?? 1), InputTypes.ClassNumber);
        var replace = ReplaceCheck(form, has, "Replace the headers and footers the document has");
        int count = doc.PageCount;

        ShowFormDialog("Header and footer", form, "Save as…", () =>
        {
            if (!TryNumber(size, "Font size", 4, 72, out double points)
                || !TryNumber(vertical, "Distance from the top and bottom", 0, 100, out double v)
                || !TryNumber(horizontal, "Distance from the sides", 0, 100, out double h)
                || !TryWhole(first, "Number of the first page", 0, 1_000_000, out int start))
                return false;
            var design = new PdfHeaderFooter
            {
                TopLeft = topLeft.Text ?? "", TopCenter = topCenter.Text ?? "", TopRight = topRight.Text ?? "",
                BottomLeft = bottomLeft.Text ?? "", BottomCenter = bottomCenter.Text ?? "", BottomRight = bottomRight.Text ?? "",
                Format = new PdfTextFormat(old?.Format.FontFamily ?? "Arial", points),
                TopMargin = v * PointsPerMm, BottomMargin = v * PointsPerMm,
                LeftMargin = h * PointsPerMm, RightMargin = h * PointsPerMm,
                StartPageNumber = start,
                DateFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern,
                FileName = _fileName ?? BaseName + ".pdf",
            };
            if (design.IsEmpty)
            {
                Toast.MakeText(this, "Type the text of a header or a footer.", ToastLength.Short)?.Show();
                return false;
            }
            SaveMarked(doc, bytes, $"{BaseName} with header and footer.pdf", "Adding the header and footer…",
                $"The header and footer were added to the {count} pages of a new PDF.",
                engine => replace?.Checked == true ? PdfPageMarks.Update(engine, bytes, design) : PdfPageMarks.Add(engine, bytes, design));
            return true;
        });
    }

    /// <summary>Bates numbering: prefix, first number and digits, in a corner or the middle of the top or bottom.</summary>
    private void ShowBatesDialog(ViewerDocument doc, byte[] bytes, PdfPageMarksInfo info)
    {
        var old = info.Bates;
        bool has = info.Has(PdfMarkKind.Bates);
        var form = FormColumn();
        FormNote(form, "A number on every page that runs on from the first: the prefix, then the number with leading zeros. " + MarksNote(doc));
        var prefix = FormField(form, "Prefix", old?.Bates?.Prefix ?? "", InputTypes.ClassText | InputTypes.TextFlagCapCharacters, "e.g. ABC");
        var start = FormField(form, "First number", (old?.Bates?.Start ?? 1).ToString(CultureInfo.InvariantCulture), InputTypes.ClassNumber);
        var digits = FormField(form, "Digits", Num(old?.Bates?.Digits ?? 6), InputTypes.ClassNumber);
        var example = FormNote(form, "");
        void Show()
        {
            long.TryParse((start.Text ?? "").Trim(), out long s);
            int.TryParse((digits.Text ?? "").Trim(), out int d);
            example.Text = "The first page shows: " + new PdfBatesNumbering(prefix.Text ?? "", "", Math.Max(0, s), Math.Clamp(d, 1, 20)).Format(Math.Max(0, s));
        }
        prefix.TextChanged += (_, _) => Show();
        start.TextChanged += (_, _) => Show();
        digits.TextChanged += (_, _) => Show();
        Show();
        string[] places = ["Bottom right", "Bottom centre", "Bottom left", "Top right", "Top centre", "Top left"];
        var place = Choice(form, "Where", places, old == null ? 0 : old.BottomCenter.Contains("<<Bates>>") ? 1 : old.BottomLeft.Contains("<<Bates>>") ? 2
            : old.TopRight.Contains("<<Bates>>") ? 3 : old.TopCenter.Contains("<<Bates>>") ? 4 : old.TopLeft.Contains("<<Bates>>") ? 5 : 0);
        var size = FormField(form, "Font size (points)", Num(old?.Format.Size ?? 10), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
        var replace = ReplaceCheck(form, has, "Replace the Bates numbers the document has");

        ShowFormDialog("Bates numbering", form, "Save as…", () =>
        {
            if (!long.TryParse((start.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long first) || first < 0 || first > 999_999_999_999)
            {
                Toast.MakeText(this, "First number: type a whole number, 0 or more.", ToastLength.Long)?.Show();
                start.RequestFocus();
                return false;
            }
            if (!TryWhole(digits, "Digits", 1, 15, out int d) || !TryNumber(size, "Font size", 4, 72, out double points)) return false;
            const string code = "<<Bates>>";
            int at = place.SelectedItemPosition;
            var design = new PdfHeaderFooter
            {
                Bates = new PdfBatesNumbering((prefix.Text ?? "").Trim(), old?.Bates?.Suffix ?? "", first, d),
                BottomRight = at == 0 ? code : "", BottomCenter = at == 1 ? code : "", BottomLeft = at == 2 ? code : "",
                TopRight = at == 3 ? code : "", TopCenter = at == 4 ? code : "", TopLeft = at == 5 ? code : "",
                Format = new PdfTextFormat(old?.Format.FontFamily ?? "Arial", points),
                TopMargin = old?.TopMargin ?? 36, BottomMargin = old?.BottomMargin ?? 36,
                LeftMargin = old?.LeftMargin ?? 72, RightMargin = old?.RightMargin ?? 72,
            };
            SaveMarked(doc, bytes, $"{BaseName} Bates.pdf", "Adding Bates numbers…", "Bates numbers were added in a new PDF.",
                engine => replace?.Checked == true ? PdfPageMarks.Update(engine, bytes, design) : PdfPageMarks.Add(engine, bytes, design));
            return true;
        });
    }

    /// <summary>The watermark colours offered, as RGB (0 to 1).</summary>
    private static readonly (string Name, double R, double G, double B)[] WatermarkColours =
    [
        ("Grey", 0.5, 0.5, 0.5),
        ("Red", 0.8, 0.1, 0.1),
        ("Blue", 0.12, 0.37, 0.66),
        ("Black", 0, 0, 0),
    ];

    /// <summary>Watermark: text across the middle of every page, with opacity, angle and size (a share of the page).</summary>
    private void ShowWatermarkDialog(ViewerDocument doc, byte[] bytes, PdfPageMarksInfo info)
    {
        var old = info.Watermark is { Source: PdfMarkSource.Text } w ? w : null;
        bool has = info.Has(PdfMarkKind.Watermark);
        var form = FormColumn();
        FormNote(form, MarksNote(doc));
        var text = FormField(form, "Text", old?.Text ?? "CONFIDENTIAL", InputTypes.ClassText | InputTypes.TextFlagCapCharacters);
        var number = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal;
        var opacity = FormField(form, "Opacity (%)", Num((old?.Opacity ?? 0.3) * 100, 0), number);
        var angle = FormField(form, "Angle (degrees, anticlockwise)", Num(old?.RotationDegrees ?? 45, 0), number | InputTypes.NumberFlagSigned);
        var size = FormField(form, "Size (% of the page)", Num((old?.RelativeScale ?? 0.6) * 100, 0), number);
        int colour = old == null ? 0 : Math.Max(0, Array.FindIndex(WatermarkColours, c =>
            Math.Abs(c.R - old.Format.Red) < 0.02 && Math.Abs(c.G - old.Format.Green) < 0.02 && Math.Abs(c.B - old.Format.Blue) < 0.02));
        var colours = Choice(form, "Colour", WatermarkColours.Select(c => c.Name).ToArray(), colour);
        var behind = new CheckBox(this) { Text = "Behind the page's text and pictures", Checked = old?.Behind ?? false };
        behind.SetMinHeight((int)Dp(48));
        form.AddView(behind);
        var replace = ReplaceCheck(form, has, "Replace the watermarks the document has");

        ShowFormDialog("Watermark", form, "Save as…", () =>
        {
            string words = (text.Text ?? "").Trim();
            if (words.Length == 0)
            {
                Toast.MakeText(this, "Type the watermark's text.", ToastLength.Short)?.Show();
                text.RequestFocus();
                return false;
            }
            if (!TryNumber(opacity, "Opacity", 5, 100, out double o) || !TryNumber(angle, "Angle", -360, 360, out double a)
                || !TryNumber(size, "Size", 5, 100, out double s))
                return false;
            var c = WatermarkColours[Math.Max(0, colours.SelectedItemPosition)];
            var design = new PdfWatermark
            {
                Source = PdfMarkSource.Text,
                Text = words,
                Format = new PdfTextFormat(old?.Format.FontFamily ?? "Arial", 48, Bold: true, Red: c.R, Green: c.G, Blue: c.B),
                Opacity = o / 100,
                RotationDegrees = a,
                RelativeScale = s / 100,
                Behind = behind.Checked,
            };
            SaveMarked(doc, bytes, $"{BaseName} watermarked.pdf", "Adding the watermark…", "The watermark was added in a new PDF.",
                engine => replace?.Checked == true ? PdfPageMarks.Update(engine, bytes, design) : PdfPageMarks.Add(engine, bytes, design));
            return true;
        });
    }

    /// <summary>Remove marks: the kinds the document has, all ticked; the ticked ones are removed from every page.</summary>
    private void ShowRemoveMarksDialog(ViewerDocument doc, byte[] bytes, PdfPageMarksInfo info)
    {
        var kinds = new[] { PdfMarkKind.HeaderFooter, PdfMarkKind.Bates, PdfMarkKind.Watermark, PdfMarkKind.Background }.Where(info.Has).ToArray();
        string Name(PdfMarkKind kind)
        {
            int pages = info.PagesWith(kind).Count;
            string what = kind switch
            {
                PdfMarkKind.HeaderFooter => "Headers and footers",
                PdfMarkKind.Bates => "Bates numbers",
                PdfMarkKind.Watermark => "Watermarks",
                _ => "Backgrounds",
            };
            return $"{what} ({pages} page{(pages == 1 ? "" : "s")})";
        }
        var ticked = kinds.Select(_ => true).ToArray();
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Remove marks")!
            .SetMultiChoiceItems(kinds.Select(Name).ToArray(), ticked, (_, e) => ticked[e.Which] = e.IsChecked)!
            .SetPositiveButton("Save as…", (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            var remove = kinds.Where((_, i) => ticked[i]).ToList();
            if (remove.Count == 0)
            {
                Toast.MakeText(this, "Tick the marks to remove.", ToastLength.Short)?.Show();
                return;
            }
            dialog.Dismiss();
            if (!ReferenceEquals(doc, _doc)) return;
            SaveNewPdf($"{BaseName} without marks.pdf", "Removing marks…", "The marks were removed in a new PDF.", token =>
            {
                byte[] current = bytes;
                foreach (var kind in remove)
                {
                    token.ThrowIfCancellationRequested();
                    using var engine = OpenForTool(current);
                    current = PdfPageMarks.Remove(engine, current, kind).Bytes;
                }
                if (ReferenceEquals(current, bytes)) throw new UserMessageException("Nothing was removed.");
                return current;
            });
        };
    }

    /// <summary>Writes the marked document as a new PDF; an edit that changed nothing is told instead (with the engine's reason).</summary>
    private void SaveMarked(ViewerDocument doc, byte[] bytes, string name, string working, string done, Func<PdfEngine.Vector.Document.PdfVectorDocument, PdfContentEditResult> mark)
    {
        if (!ReferenceEquals(doc, _doc)) return;
        SaveNewPdf(name, working, done, token =>
        {
            using var engine = OpenForTool(bytes);
            token.ThrowIfCancellationRequested();
            var result = mark(engine);
            if (ReferenceEquals(result.Bytes, bytes) || result.Bytes.Length == 0)
                throw new UserMessageException(result.Warnings.FirstOrDefault() ?? "Nothing changed.");
            return result.Bytes;
        });
    }

    /// <summary>"Replace the … the document has", ticked, when it has marks of the kind; null when it has none (they are added).</summary>
    private CheckBox? ReplaceCheck(LinearLayout form, bool has, string text)
    {
        if (!has) return null;
        var check = new CheckBox(this) { Text = text, Checked = true };
        check.SetMinHeight((int)Dp(48));
        form.AddView(check);
        return check;
    }

    /// <summary>A labelled drop-down list in a form.</summary>
    private Spinner Choice(LinearLayout form, string label, string[] items, int selected)
    {
        var caption = new TextView(this) { Text = label };
        caption.SetTextColor(TextColor);
        caption.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        caption.SetPadding(0, (int)Dp(8), 0, 0);
        form.AddView(caption);
        var spinner = new Spinner(this) { ContentDescription = label };
        spinner.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, items);
        spinner.SetSelection(Math.Clamp(selected, 0, items.Length - 1));
        spinner.SetMinimumHeight((int)Dp(48));
        form.AddView(spinner);
        return spinner;
    }

    /// <summary>A number for a field: whole when it is whole, else to <paramref name="decimals"/> places, in the reader's culture.</summary>
    private static string Num(double value, int decimals = 1) =>
        Math.Round(value, decimals).ToString("0.#", CultureInfo.CurrentCulture);
}
