using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PdfViewer.Themes;

/// <summary>
/// Every icon the application draws, as a character of the Windows icon font.
///
/// Windows 11 ships "Segoe Fluent Icons" and Windows 10 ships "Segoe MDL2 Assets"; the two
/// share code points, so one list serves both and the icons match the rest of the OS with no
/// image files or third-party licences. Each glyph below was chosen to exist in both fonts
/// (a test checks this), because a code point missing from the fallback font draws as an
/// empty box on Windows 10.
///
/// XAML reads these with <c>{x:Static themes:IconGlyphs.Open}</c> and draws them with one of
/// the glyph styles in IconGlyphs.xaml, so a glyph means the same thing wherever it appears.
/// </summary>
public static class IconGlyphs
{
    /// <summary>The icon font with its Windows 10 fallback, in the order WPF should try them.</summary>
    public const string FontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static readonly FontFamily IconFont = new(FontFamilyName);

    // File
    public const string Open = "";
    public const string OpenSample = "";
    public const string Recent = "";
    public const string CloseDocument = "";
    public const string Save = "";
    public const string SaveAs = "";
    public const string Export = "";
    public const string ExportImages = "";
    public const string ExportText = "";
    public const string WordDocument = "";
    public const string ExcelWorkbook = "";
    public const string PowerPointPresentation = "";
    public const string OrganizePages = "";
    public const string Attachments = "";
    public const string Print = "";
    public const string Properties = "";
    public const string Exit = "";

    // Edit
    public const string Copy = "";
    public const string Highlight = "";
    public const string Underline = "";
    public const string Strikeout = "";
    public const string SelectAll = "";
    public const string Deselect = "";
    public const string Redact = "";
    public const string SelectText = "";
    public const string MarkArea = "";
    public const string ApplyRedactions = "";
    public const string ClearMarks = "";
    public const string RedactNow = "";
    public const string EditContent = "";
    public const string AddText = "";
    public const string AddImage = "";
    public const string PageMarks = "";
    public const string HeaderFooter = "";
    public const string Watermark = "";
    public const string Background = "";
    public const string BatesNumbering = "";
    public const string Undo = "";
    public const string Redo = "";
    public const string Delete = "";

    // View
    public const string Back = "";
    public const string Forward = "";
    public const string ReadMode = "";
    public const string ShowFormFields = "";
    public const string FullScreen = "";
    public const string Continuous = "";
    public const string SinglePage = "";
    public const string FacingPages = "";
    public const string CoverPage = "";
    public const string ZoomIn = "";
    public const string ZoomOut = "";
    public const string FitWidth = "";
    public const string FitPage = "";

    /// <summary>
    /// Clockwise. The font has no counter-clockwise twin, so that one is this glyph mirrored
    /// (see the MirroredMenuGlyph and MirroredToolbarGlyph styles), which keeps the two a matched pair.
    /// </summary>
    public const string Rotate = "";
    public const string Sidebar = "";
    public const string Theme = "";
    public const string NightMode = "";
    public const string RenderingEngine = "";
    public const string EngineAuto = "";
    public const string EngineVector = "";
    public const string EngineRaster = "";

    // Navigation
    public const string FirstPage = "";
    public const string PreviousPage = "";
    public const string NextPage = "";
    public const string LastPage = "";
    public const string PreviousMatch = "";
    public const string NextMatch = "";
    public const string Close = "";
    public const string Check = "";

    // Tools
    public const string Find = "";
    public const string Pan = "";
    public const string Ocr = "";
    public const string MakeSearchable = "";
    public const string ReadAloud = "";
    public const string Pause = "";
    public const string DocumentSafety = "";
    public const string PdfA = "";
    public const string Accessibility = "";
    public const string ReduceSize = "";
    public const string CleanCopy = "";
    public const string Protect = "";
    public const string RemoveSecurity = "";
    public const string Sign = "";
    public const string VerifySignatures = "";
    public const string Merge = "";
    public const string Split = "";
    public const string ExtractPage = "";
    public const string FillForm = "";
    public const string Compare = "";

    // Annotation tools
    public const string Note = "";
    public const string TextBox = "";
    public const string Ink = "";
    public const string Shape = "";

    // Sidebar
    public const string Pages = "";
    public const string Bookmarks = "";
    public const string SearchResults = "";
    public const string Annotations = "";
    public const string Signatures = "";

    // Help
    public const string Privacy = "";
    public const string CheckForUpdates = "";
    public const string SourceCode = "";
    public const string Releases = "";
    public const string About = "";
    public const string Help = "";
    public const string Settings = "";

    /// <summary>
    /// The glyph a sidebar tab (or any element) shows next to, or instead of, its label.
    /// </summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(IconGlyphs), new FrameworkPropertyMetadata(null));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);

    /// <summary>
    /// A menu icon for menus built in code. It takes the menu's own text colour, as the XAML
    /// menu icons do, so it is right in every theme and greys out with a disabled item.
    /// </summary>
    public static TextBlock CreateMenuIcon(string glyph, bool mirrored = false) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = mirrored ? new ScaleTransform(-1, 1) : Transform.Identity,
        Focusable = false,
    };
}
