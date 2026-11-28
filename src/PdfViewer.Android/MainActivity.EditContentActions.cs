using Android.App;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Geometry;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// The edits of "Edit text and images": a paragraph's text, moving, resizing, turning, replacing and
/// deleting, new text and pictures, Undo and Save. Each edit is applied by the engine to the last
/// revision on a worker thread and the page is drawn again from the result.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The largest picture file Add picture and Replace take.</summary>
    private const long MaxContentPictureBytes = 64L * 1024 * 1024;

    /// <summary>Pictures are decoded at most this wide or high (a page needs no more).</summary>
    private const int MaxContentPictureSide = 2400;

    /// <summary>How the last new text looked (the next starts the same).</summary>
    private int _newTextSize = 12, _newTextFont, _newTextColour;
    private bool _newTextBold;

    private static readonly (string Name, string Family)[] NewTextFonts =
    [
        ("Sans serif", "Arial"), ("Serif", "Times New Roman"), ("Monospace", "Courier New"),
    ];

    private static readonly (string Name, double R, double G, double B)[] NewTextColours =
    [
        ("Black", 0, 0, 0), ("Dark blue", 0.08, 0.2, 0.55), ("Red", 0.75, 0.1, 0.1), ("Grey", 0.4, 0.4, 0.4),
    ];

    private static readonly int[] NewTextSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72];

    // ------------------------------------------------------------------ taps

    private void ContentTapped(ContentEditor editor, ContentEditView.Box? box, PointF point)
    {
        if (editor.Placing == ContentPlacing.Text)
        {
            EndContentPlacing(editor);
            AskNewText(editor, point);
            return;
        }
        if (editor.Placing == ContentPlacing.Picture)
        {
            var picture = editor.PendingPicture;
            EndContentPlacing(editor);
            if (picture != null) _ = AddContentPictureAsync(editor, picture, point);
            return;
        }
        SelectContent(editor, box);
        if (box is { Kind: PdfEditTarget.Text }) EditContentText(editor, box);
    }

    private void BeginContentPlacing(ContentEditor editor, ContentPlacing placing)
    {
        editor.Placing = placing;
        editor.View.Placing = true;
        editor.View.Select(null);
        UpdateContentChrome(editor);
    }

    private void EndContentPlacing(ContentEditor editor)
    {
        editor.Placing = ContentPlacing.None;
        editor.PendingPicture = null;
        editor.View.Placing = false;
        UpdateContentChrome(editor);
    }

    /// <summary>The paragraphs and pictures of the page as a list (for TalkBack and for small items).</summary>
    private void ShowContentList(ContentEditor editor)
    {
        var boxes = editor.View.Boxes;
        if (boxes.Count == 0 || editor.Items is not { } items)
        {
            Toast.MakeText(this, "This page has no text or pictures that can be edited. Add text or a picture instead.", ToastLength.Long)?.Show();
            return;
        }
        int picture = 0;
        var names = boxes.Select(b =>
        {
            if (b.Kind == PdfEditTarget.Image)
            {
                var image = items.Images.FirstOrDefault(i => i.Id == b.Id);
                picture++;
                return image == null ? $"Picture {picture}" : $"Picture {picture} ({image.PixelWidth} × {image.PixelHeight} pixels)";
            }
            string text = items.Texts.FirstOrDefault(t => t.Id == b.Id)?.Text.ReplaceLineEndings(" ").Trim() ?? "";
            return "Text: " + (text.Length > 60 ? text[..60] + "…" : text);
        }).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle($"Page {editor.Page}")!
            .SetItems(names, (_, e) =>
            {
                var box = boxes[e.Which];
                SelectContent(editor, box);
                if (box.Kind == PdfEditTarget.Text) EditContentText(editor, box);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ text

    /// <summary>A paragraph's text in a dialog; the new text reflows in the paragraph and keeps its look. Empty deletes it.</summary>
    private void EditContentText(ContentEditor editor, ContentEditView.Box box)
    {
        var paragraph = editor.Items?.Texts.FirstOrDefault(t => t.Id == box.Id);
        if (paragraph == null) return;
        var input = new EditText(this)
        {
            Text = paragraph.Text,
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
            ContentDescription = "Text of the paragraph",
        };
        input.SetMinLines(3);
        input.SetMaxLines(10);
        input.Gravity = GravityFlags.Top | GravityFlags.Start;
        var frame = new FrameLayout(this);
        frame.SetPadding((int)Dp(20), (int)Dp(8), (int)Dp(20), 0);
        frame.AddView(input);
        new AlertDialog.Builder(this)
            .SetTitle("Edit text")!
            .SetMessage("The text keeps its font, size and colour, and reflows within the paragraph's width.")!
            .SetView(frame)!
            .SetPositiveButton("Change", (_, _) =>
            {
                string text = (input.Text ?? "").Replace("\r\n", "\n");
                if (text == paragraph.Text) return;
                bool delete = text.Trim().Length == 0;
                _ = ApplyContentEditAsync(editor, new PdfReplaceText(editor.Page, box.Id, text), delete ? "Paragraph deleted." : "Text changed.");
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
        input.RequestFocus();
    }

    /// <summary>New text at a point: the text, its size, font, colour and weight.</summary>
    private void AskNewText(ContentEditor editor, PointF at)
    {
        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(20), (int)Dp(8), (int)Dp(20), 0);
        var input = new EditText(this)
        {
            Hint = "Text",
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
            ContentDescription = "The new text",
        };
        input.SetMinLines(2);
        input.SetMaxLines(8);
        input.Gravity = GravityFlags.Top | GravityFlags.Start;
        column.AddView(input);

        Spinner Choice(string label, string[] items, int selected)
        {
            var row = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight((int)Dp(48));
            var name = new TextView(this) { Text = label };
            name.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
            name.SetMinWidth((int)Dp(64));
            row.AddView(name);
            var spinner = new Spinner(this) { ContentDescription = label };
            spinner.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, items);
            spinner.SetSelection(selected);
            spinner.SetMinimumHeight((int)Dp(48));
            row.AddView(spinner, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            column.AddView(row);
            return spinner;
        }

        int sizeIndex = Math.Max(0, Array.IndexOf(NewTextSizes, _newTextSize));
        var size = Choice("Size", NewTextSizes.Select(s => $"{s} pt").ToArray(), sizeIndex);
        var font = Choice("Font", NewTextFonts.Select(f => f.Name).ToArray(), _newTextFont);
        var colour = Choice("Colour", NewTextColours.Select(c => c.Name).ToArray(), _newTextColour);
        var bold = new CheckBox(this) { Text = "Bold", Checked = _newTextBold };
        bold.SetMinHeight((int)Dp(48));
        column.AddView(bold);
        var scroll = new ScrollView(this);
        scroll.AddView(column);

        new AlertDialog.Builder(this)
            .SetTitle("Add text")!
            .SetView(scroll)!
            .SetPositiveButton("Add", (_, _) =>
            {
                string text = (input.Text ?? "").Replace("\r\n", "\n");
                if (text.Trim().Length == 0) return;
                _newTextSize = NewTextSizes[Math.Clamp(size.SelectedItemPosition, 0, NewTextSizes.Length - 1)];
                _newTextFont = Math.Clamp(font.SelectedItemPosition, 0, NewTextFonts.Length - 1);
                _newTextColour = Math.Clamp(colour.SelectedItemPosition, 0, NewTextColours.Length - 1);
                _newTextBold = bold.Checked;
                var g = editor.Geometry;
                // Upright as the page is shown: the baseline runs along the page's rotation, the first
                // line's top at the point tapped.
                int rotation = g.Rotation is 90 or 180 or 270 ? g.Rotation : 0;
                double angle = rotation * Math.PI / 180, points = _newTextSize;
                var (x, y) = g.ToUser(at.X, at.Y);
                var baseline = new PdfPoint(x + Math.Sin(angle) * 0.8 * points, y - Math.Cos(angle) * 0.8 * points);
                var c = NewTextColours[_newTextColour];
                var format = new PdfTextFormat(NewTextFonts[_newTextFont].Family, points, _newTextBold, false, c.R, c.G, c.B, rotation);
                _ = ApplyContentEditAsync(editor, new PdfAddText(editor.Page, baseline, text, format), "Text added.");
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
        input.RequestFocus();
    }

    // ------------------------------------------------------------------ moving, resizing, turning, deleting

    private Task MoveContentAsync(ContentEditor editor, ContentEditView.Box box, float dx, float dy)
    {
        var g = editor.Geometry;
        var a = g.ToUser(0, 0);
        var b = g.ToUser(dx, dy);
        var t = PdfMatrix.CreateTranslation(b.X - a.X, b.Y - a.Y);
        return ApplyContentEditAsync(editor, new PdfTransformContent(editor.Page, box.Kind, box.Id, t),
            box.Kind == PdfEditTarget.Text ? "Paragraph moved." : "Picture moved.",
            (box.Kind, box.Shown.CenterX() + dx, box.Shown.CenterY() + dy));
    }

    private Task ResizeContentAsync(ContentEditor editor, ContentEditView.Box box, RectF to)
    {
        var g = editor.Geometry;
        var from = UserRect(g, box.Shown);
        var target = UserRect(g, to);
        if (from.Width <= 0 || from.Height <= 0 || target.Width <= 0 || target.Height <= 0) return Task.CompletedTask;
        var t = PdfMatrix.CreateTranslation(-from.X, -from.Y) * PdfMatrix.CreateScale(target.Width / from.Width, target.Height / from.Height)
                * PdfMatrix.CreateTranslation(target.X, target.Y);
        return ApplyContentEditAsync(editor, new PdfTransformContent(editor.Page, box.Kind, box.Id, t), "Picture resized.",
            (box.Kind, to.CenterX(), to.CenterY()));
    }

    /// <summary>Scales a picture about its centre (the Bigger and Smaller buttons).</summary>
    private Task ScaleContentAsync(ContentEditor editor, ContentEditView.Box box, double factor)
    {
        var r = UserRect(editor.Geometry, box.Shown);
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        var t = PdfMatrix.CreateTranslation(-cx, -cy) * PdfMatrix.CreateScale(factor, factor) * PdfMatrix.CreateTranslation(cx, cy);
        return ApplyContentEditAsync(editor, new PdfTransformContent(editor.Page, box.Kind, box.Id, t),
            factor > 1 ? "Picture made bigger." : "Picture made smaller.", (box.Kind, box.Shown.CenterX(), box.Shown.CenterY()));
    }

    /// <summary>Turns a picture a quarter turn clockwise as seen, about its centre.</summary>
    private Task TurnContentAsync(ContentEditor editor, ContentEditView.Box box)
    {
        var r = UserRect(editor.Geometry, box.Shown);
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        // Clockwise in user space (y up) is clockwise as shown: /Rotate turns the page without mirroring it.
        var t = PdfMatrix.CreateTranslation(-cx, -cy) * PdfMatrix.CreateRotation(-Math.PI / 2) * PdfMatrix.CreateTranslation(cx, cy);
        return ApplyContentEditAsync(editor, new PdfTransformContent(editor.Page, box.Kind, box.Id, t), "Picture turned.",
            (box.Kind, box.Shown.CenterX(), box.Shown.CenterY()));
    }

    private Task DeleteContentAsync(ContentEditor editor, ContentEditView.Box box) =>
        ApplyContentEditAsync(editor, new PdfDeleteContent(editor.Page, box.Kind, box.Id),
            (box.Kind == PdfEditTarget.Text ? "Paragraph deleted." : "Picture deleted.") + " Undo brings it back.");

    // ------------------------------------------------------------------ pictures

    private void ReplaceContentPicture(ContentEditor editor, ContentEditView.Box box)
    {
        PickFiles(["image/*"], false, async uris =>
        {
            var picture = await RunBusyAsync("Reading the picture…", _ => ReadContentPicture(uris[0]));
            if (picture == null || !ReferenceEquals(editor, _contentEditor)) return;
            await ApplyContentEditAsync(editor, new PdfReplaceImage(editor.Page, box.Id, picture), "Picture replaced.",
                (box.Kind, box.Shown.CenterX(), box.Shown.CenterY()));
        });
    }

    /// <summary>Add picture: the picture is picked and read first, then a tap places it.</summary>
    private void PickContentPicture(ContentEditor editor)
    {
        PickFiles(["image/*"], false, async uris =>
        {
            var picture = await RunBusyAsync("Reading the picture…", _ => ReadContentPicture(uris[0]));
            if (picture == null || !ReferenceEquals(editor, _contentEditor)) return;
            BeginContentPlacing(editor, ContentPlacing.Picture);
            editor.PendingPicture = picture;
        });
    }

    /// <summary>
    /// A new picture centred at a point, upright as the page is shown: at most half the page in each
    /// direction and at most one point per pixel (72 dpi), as the desktop viewer places it.
    /// </summary>
    private Task AddContentPictureAsync(ContentEditor editor, PdfImageContent picture, PointF at)
    {
        var g = editor.Geometry;
        int rotation = g.Rotation is 90 or 180 or 270 ? g.Rotation : 0;
        double scale = Math.Min(1.0, Math.Min(g.ShownWidth * 0.5 / picture.Width, g.ShownHeight * 0.5 / picture.Height));
        double w = picture.Width * scale, h = picture.Height * scale;
        var (x, y) = g.ToUser(at.X, at.Y);
        var bounds = new PdfRect(x - w / 2, y - h / 2, w, h);
        return ApplyContentEditAsync(editor, new PdfAddImage(editor.Page, bounds, picture, rotation), "Picture added.",
            (PdfEditTarget.Image, at.X, at.Y));
    }

    /// <summary>
    /// A picked picture decoded by Android (JPEG, PNG, WebP, GIF, HEIC…), turned by its EXIF
    /// orientation and made at most <see cref="MaxContentPictureSide"/> pixels on a side: an opaque
    /// photo as a JPEG, a drawing or a picture with transparency losslessly (worker thread).
    /// </summary>
    private PdfImageContent ReadContentPicture(Uri uri)
    {
        byte[] file = ReadUri(uri, MaxContentPictureBytes);
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(file, 0, file.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            throw new UserMessageException("This is not a picture this device can read.");
        bool lossless = bounds.OutMimeType is "image/png" or "image/gif" or "image/bmp" or "image/x-ms-bmp";
        int sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= MaxContentPictureSide) sample *= 2;
        var bitmap = BitmapFactory.DecodeByteArray(file, 0, file.Length, new BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = Bitmap.Config.Argb8888 })
                     ?? throw new UserMessageException("The picture cannot be read: it is damaged.");
        try
        {
            using var matrix = new Matrix();
            float fit = Math.Min(1f, (float)MaxContentPictureSide / Math.Max(bitmap.Width, bitmap.Height));
            if (fit < 1f) matrix.PostScale(fit, fit);
            ApplyExifOrientation(matrix, file);
            if (!matrix.IsIdentity)
            {
                var turned = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true)
                             ?? throw new UserMessageException("The picture cannot be read: it is damaged.");
                if (!ReferenceEquals(turned, bitmap))
                {
                    bitmap.Recycle();
                    bitmap = turned;
                }
            }
            int w = bitmap.Width, h = bitmap.Height;
            // GetPixels gives unpremultiplied 0xAARRGGBB ints.
            var argb = new int[w * h];
            bitmap.GetPixels(argb, 0, w, 0, 0, w, h);
            bool opaque = argb.All(c => (c >>> 24) == 255);
            if (opaque && !lossless)
            {
                using var jpeg = new MemoryStream();
                if (bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 90, jpeg))
                    return new PdfImageContent(w, h, jpeg.ToArray(), PdfImageEncoding.Jpeg, null, 3);
            }
            var rgb = new byte[(long)w * h * 3];
            var alpha = opaque ? null : new byte[(long)w * h];
            for (int i = 0; i < argb.Length; i++)
            {
                int c = argb[i];
                rgb[i * 3] = (byte)(c >> 16);
                rgb[i * 3 + 1] = (byte)(c >> 8);
                rgb[i * 3 + 2] = (byte)c;
                if (alpha != null) alpha[i] = (byte)(c >>> 24);
            }
            return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, alpha, 3);
        }
        finally
        {
            bitmap.Recycle();
        }
    }

    /// <summary>Adds the turn or mirror a photo's EXIF orientation asks for (none when it has no readable EXIF).</summary>
    private static void ApplyExifOrientation(Matrix matrix, byte[] file)
    {
        int orientation;
        try
        {
            using var stream = new MemoryStream(file, writable: false);
            var exif = new global::Android.Media.ExifInterface(stream);
            orientation = exif.GetAttributeInt(global::Android.Media.ExifInterface.TagOrientation, 1);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            return;
        }
        switch (orientation)
        {
            case 2: matrix.PostScale(-1, 1); break;
            case 3: matrix.PostRotate(180); break;
            case 4: matrix.PostScale(1, -1); break;
            case 5: matrix.PostRotate(90); matrix.PostScale(-1, 1); break;
            case 6: matrix.PostRotate(90); break;
            case 7: matrix.PostRotate(270); matrix.PostScale(-1, 1); break;
            case 8: matrix.PostRotate(270); break;
        }
    }

    // ------------------------------------------------------------------ applying, undo, save

    /// <summary>
    /// Applies one edit to the last revision on a worker thread and shows the page drawn from the
    /// result; <paramref name="reselect"/> selects the item that went there (kind and centre, page as shown).
    /// A signed document is warned about before its first edit.
    /// </summary>
    private async Task ApplyContentEditAsync(ContentEditor editor, PdfContentEdit edit, string done, (PdfEditTarget Kind, float X, float Y)? reselect = null)
    {
        if (!ReferenceEquals(editor, _contentEditor)) return;
        if (editor.Doc.IsSigned && !editor.SignedConfirmed)
        {
            new AlertDialog.Builder(this)
                .SetTitle("Edit a signed document?")!
                .SetMessage("This document is signed. The edits are added after the signatures, which will then show that the document was changed after signing.")!
                .SetPositiveButton("Edit anyway", (_, _) =>
                {
                    editor.SignedConfirmed = true;
                    _ = ApplyContentEditAsync(editor, edit, done, reselect);
                })!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .Show();
            return;
        }
        byte[] bytes = editor.Current;
        var (w, h) = ContentViewSize(editor);
        string? password = _password;
        var result = await RunBusyAsync("Editing…", token =>
        {
            editor.Open(bytes, OpenForTool, password, token);
            var applied = PdfContentEditor.Apply(editor.Engine!, bytes, [edit], new PdfContentEditOptions { Fonts = SystemFontCatalog.Installed });
            if (ReferenceEquals(applied.Bytes, bytes))
                throw new UserMessageException("Nothing was changed: this could not be edited.");
            token.ThrowIfCancellationRequested();
            var page = ReadContentPage(editor, applied.Bytes, edit.PageNumber, w, h, password, token);
            return (Bytes: applied.Bytes, Page: page, Fonts: applied.EmbeddedFonts);
        });
        if (!ReferenceEquals(editor, _contentEditor))
        {
            result.Page?.Bitmap.Recycle();
            return;
        }
        if (result.Bytes == null)
        {
            // Failed or cancelled: the screen still shows the last revision; reselect nothing.
            editor.View.Select(null);
            UpdateContentChrome(editor);
            return;
        }
        editor.Revisions.Add(result.Bytes);
        editor.Descriptions.Add(done);
        ShowContentPage(editor, result.Page, reselect);
        string fonts = result.Fonts.Count > 0 ? $" Embedded {string.Join(", ", result.Fonts)} for characters the document's fonts lack." : "";
        Toast.MakeText(this, done + fonts, fonts.Length > 0 ? ToastLength.Long : ToastLength.Short)?.Show();
    }

    /// <summary>Undo: back to the revision before the last edit, and its page drawn again.</summary>
    private async Task UndoContentEditAsync(ContentEditor editor)
    {
        if (editor.Revisions.Count <= 1) return;
        string undone = editor.Descriptions[^1];
        editor.Revisions.RemoveAt(editor.Revisions.Count - 1);
        editor.Descriptions.RemoveAt(editor.Descriptions.Count - 1);
        if (editor.SavedRevision >= editor.Revisions.Count) editor.SavedRevision = -1; // the saved one is gone: unsaved again
        editor.Placing = ContentPlacing.None;
        editor.PendingPicture = null;
        editor.View.Placing = false;
        await LoadContentPageAsync(editor);
        if (ReferenceEquals(editor, _contentEditor))
            Toast.MakeText(this, "Undone: " + undone.Replace(" Undo brings it back.", "").TrimEnd('.').ToLowerInvariant() + ".", ToastLength.Short)?.Show();
    }

    /// <summary>Save: the last revision (the document with the edits added as an incremental update) as a new PDF.</summary>
    private void SaveContentEdits(ContentEditor editor)
    {
        if (editor.Revisions.Count <= 1)
        {
            Toast.MakeText(this, "There are no edits to save yet.", ToastLength.Short)?.Show();
            return;
        }
        byte[] bytes = editor.Current;
        int revision = editor.Revisions.Count - 1;
        string note = editor.Doc.IsSigned ? " Its signatures will show that it was changed after signing." : "";
        SaveNewFile("application/pdf", BaseName + " (edited).pdf", "Saving…", _ => bytes, target =>
        {
            if (ReferenceEquals(editor, _contentEditor) && editor.Revisions.Count - 1 >= revision && ReferenceEquals(editor.Revisions[revision], bytes))
                editor.SavedRevision = revision;
            new AlertDialog.Builder(this)
                .SetTitle("Saved")!
                .SetMessage("The edited document is saved as a new PDF; this document is not changed." + note)!
                .SetPositiveButton("Open it", (_, _) => AfterDiscard(() =>
                {
                    // The edit screen belongs to this document: it closes before the new one opens.
                    CloseContentEditorNow(editor);
                    RememberPosition();
                    _ = OpenUriAsync(target, null);
                }))!
                .SetNegativeButton("Keep editing", (_, _) => { })!
                .Show();
        });
    }
}
