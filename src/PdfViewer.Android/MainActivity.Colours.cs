using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;
using PdfViewer.Web.Engine.Annotations;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// The colour of new annotations: a Colour button in the annotate bar opens the web viewer's palette
/// (<see cref="ViewerToolColors.Palette"/>) for the chosen tool, for the chosen annotation (it is
/// recoloured, one undo step), or, with neither, for a kind the reader picks first (highlights,
/// which are made from the selection bar, among them). The last colour chosen for each kind is
/// kept in app-private preferences, so it stays after a restart; "Default" forgets it. Each colour
/// is listed with its name (TalkBack reads the name and whether it is checked) and a swatch.
/// </summary>
public sealed partial class MainActivity
{
    private const string ColourPreferences = "annotation-colours";

    private TextView _colourButton = null!;

    /// <summary>The kinds a colour is chosen for when no tool and no annotation is chosen, in the order they are listed.</summary>
    private static readonly ViewerAnnotationKind[] ColourKinds =
    [
        ViewerAnnotationKind.Highlight, ViewerAnnotationKind.Underline, ViewerAnnotationKind.StrikeOut, ViewerAnnotationKind.Ink,
        ViewerAnnotationKind.Note, ViewerAnnotationKind.FreeText, ViewerAnnotationKind.Square, ViewerAnnotationKind.Circle, ViewerAnnotationKind.Line,
    ];

    private void BuildColourButton() =>
        _colourButton = ToolButton("Colour", "Colour of new annotations", (_, _) => ChooseColour());

    /// <summary>The kind of annotation a tool makes, or null (browse, stamps, moving).</summary>
    private static ViewerAnnotationKind? KindOf(PageView.PageTool tool) => tool switch
    {
        PageView.PageTool.Ink => ViewerAnnotationKind.Ink,
        PageView.PageTool.Note => ViewerAnnotationKind.Note,
        PageView.PageTool.Text or PageView.PageTool.Callout => ViewerAnnotationKind.FreeText,
        PageView.PageTool.Rectangle => ViewerAnnotationKind.Square,
        PageView.PageTool.Ellipse => ViewerAnnotationKind.Circle,
        PageView.PageTool.Line or PageView.PageTool.Arrow => ViewerAnnotationKind.Line,
        _ => null,
    };

    /// <summary>What new annotations of a kind are called in a dialog title.</summary>
    private static string Plural(ViewerAnnotationKind kind) => kind switch
    {
        ViewerAnnotationKind.Highlight => "highlights",
        ViewerAnnotationKind.Underline => "underlines",
        ViewerAnnotationKind.StrikeOut => "strike-throughs",
        ViewerAnnotationKind.Ink => "drawings",
        ViewerAnnotationKind.Note => "notes",
        ViewerAnnotationKind.FreeText => "text boxes and callouts",
        ViewerAnnotationKind.Square => "rectangles",
        ViewerAnnotationKind.Circle => "ellipses",
        ViewerAnnotationKind.Line => "lines and arrows",
        _ => ViewerEditSession.Label(kind).ToLowerInvariant(),
    };

    private ISharedPreferences? ColourStore => GetSharedPreferences(ColourPreferences, FileCreationMode.Private);

    /// <summary>The colour kept for new annotations of a kind, or null for the kind's default (the session uses it).</summary>
    private string? KeptColour(ViewerAnnotationKind kind) =>
        ViewerToolColors.Parse(ColourStore?.GetString(ViewerToolColors.KeyOf(kind), null));

    /// <summary>The colour new annotations of a kind get: the kept one, or the kind's default.</summary>
    private string ColourOf(ViewerAnnotationKind kind) => KeptColour(kind) ?? ViewerAnnotationPdf.DefaultColor(kind);

    private void KeepColour(ViewerAnnotationKind kind, string? colour)
    {
        var editor = ColourStore?.Edit();
        if (colour == null) editor?.Remove(ViewerToolColors.KeyOf(kind));
        else editor?.PutString(ViewerToolColors.KeyOf(kind), colour);
        editor?.Apply();
        UpdateEditButtons();
    }

    /// <summary>The Colour button: for the chosen annotation, else for the chosen tool, else for a kind picked first.</summary>
    private void ChooseColour()
    {
        if (!CanAnnotate) return;
        if (_pages.PickedAnnotation is { } id && _session?.Store.Find(id) is { } a && ViewerToolColors.Choosable(a.Kind))
        {
            AskColour($"Colour of this {ViewerEditSession.Label(a).ToLowerInvariant()}", a.Color, withDefault: false,
                colour => Change(s => colour != null && s.SetColor(id, colour)));
            return;
        }
        if (KindOf(_pages.Tool) is { } kind)
        {
            AskColourFor(kind);
            return;
        }
        var lines = ColourKinds.Select(k => $"{char.ToUpperInvariant(Plural(k)[0])}{Plural(k)[1..]}: {ViewerToolColors.NameOf(ColourOf(k))}").ToArray();
        new AlertDialog.Builder(this)
            .SetTitle("Colour of new…")!
            .SetItems(lines, (_, e) => AskColourFor(ColourKinds[e.Which]))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private void AskColourFor(ViewerAnnotationKind kind) =>
        AskColour($"Colour of new {Plural(kind)}", KeptColour(kind), withDefault: true, colour => KeepColour(kind, colour),
            ViewerAnnotationPdf.DefaultColor(kind));

    /// <summary>
    /// The palette as a single-choice list (each colour named, with a swatch), the current colour
    /// checked; with <paramref name="withDefault"/>, a last item "Default" (null) that forgets the choice.
    /// </summary>
    private void AskColour(string title, string? current, bool withDefault, Action<string?> chosen, string? defaultColour = null)
    {
        var palette = ViewerToolColors.Palette;
        var names = palette.Select(p => p.Name).ToList();
        var colours = palette.Select(p => p.Value).ToList();
        if (withDefault)
        {
            names.Add($"Default ({ViewerToolColors.NameOf(defaultColour ?? "")})");
            colours.Add(defaultColour ?? "#000000");
        }
        int checkedItem = current == null && withDefault ? palette.Count : ViewerToolColors.IndexOf(current);
        var adapter = new SwatchAdapter(this, names, colours.Select(c => SwatchDrawable(c, 18)).ToList(), (int)Dp(12));
        new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetSingleChoiceItems(adapter, checkedItem, (sender, e) =>
            {
                (sender as IDialogInterface)?.Dismiss();
                chosen(e.Which < palette.Count ? palette[e.Which].Value : null);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>
    /// The palette's lines: the system's single-choice line with the colour's name as its only text
    /// (TalkBack reads the name and whether it is checked) and a round swatch of the colour before it,
    /// a drawable, which TalkBack does not read (a grey ring keeps black visible in the dark theme).
    /// </summary>
    private sealed class SwatchAdapter : ArrayAdapter<string>
    {
        private readonly IList<Drawable> _swatches;
        private readonly int _padding;

        public SwatchAdapter(Context context, IList<string> names, IList<Drawable> swatches, int padding)
            : base(context, global::Android.Resource.Layout.SelectDialogSingleChoice, names)
        {
            _swatches = swatches;
            _padding = padding;
        }

        public override global::Android.Views.View GetView(int position, global::Android.Views.View? convertView, global::Android.Views.ViewGroup parent)
        {
            var view = base.GetView(position, convertView, parent);
            if (view is TextView line)
            {
                line.SetCompoundDrawablesRelative(_swatches[position], null, null, null);
                line.CompoundDrawablePadding = _padding;
            }
            return view;
        }
    }

    private GradientDrawable SwatchDrawable(string colour, float sizeDp)
    {
        var dot = new GradientDrawable();
        dot.SetShape(ShapeType.Oval);
        dot.SetColor(Color.ParseColor(colour));
        dot.SetStroke((int)Math.Max(1, Dp(1.5f)), Color.ParseColor("#8C9199"));
        int size = (int)Dp(sizeDp);
        dot.SetBounds(0, 0, size, size);
        return dot;
    }

    /// <summary>
    /// The Colour button shows (a swatch) and says the colour it would change: the chosen
    /// annotation's, or the chosen tool's; the markup buttons show their kind's colour. The stroke
    /// being drawn takes the chosen tool's colour.
    /// </summary>
    private void UpdateColours()
    {
        if (_colourButton == null) return;
        string? shown = null, said = null;
        if (_pages.PickedAnnotation is { } id && _session?.Store.Find(id) is { } a && ViewerToolColors.Choosable(a.Kind))
        {
            shown = a.Color;
            said = $"Colour of this {ViewerEditSession.Label(a).ToLowerInvariant()}: {ViewerToolColors.NameOf(a.Color)}";
        }
        else if (KindOf(_pages.Tool) is { } kind)
        {
            shown = ColourOf(kind);
            said = $"Colour of new {Plural(kind)}: {ViewerToolColors.NameOf(shown)}";
            _pages.StrokeColor = shown;
        }
        _colourButton.ContentDescription = said ?? "Colour of new annotations";
        SetSwatch(_colourButton, shown);
        SetSwatch(_highlightButton, ColourOf(ViewerAnnotationKind.Highlight));
        SetSwatch(_underlineButton, ColourOf(ViewerAnnotationKind.Underline));
        SetSwatch(_strikeButton, ColourOf(ViewerAnnotationKind.StrikeOut));
    }

    private void SetSwatch(TextView button, string? colour)
    {
        button.SetCompoundDrawables(colour == null ? null : SwatchDrawable(colour, 12), null, null, null);
        button.CompoundDrawablePadding = (int)Dp(6);
    }
}
