using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PdfViewer.Text;

namespace PdfViewer.Views;

/// <summary>
/// Read mode: the document's text reflowed as headings, paragraphs, lists and figure
/// descriptions (from its tags when tagged), at a comfortable size that follows the window
/// width. The content is a WPF FlowDocument, which UI Automation exposes as a text document —
/// screen readers (Narrator, NVDA, JAWS) read it with full text navigation, by paragraph, line,
/// word and character. Ctrl+Plus/Minus change the size; Escape closes.
/// </summary>
internal sealed class ReadModeWindow : Window
{
    private readonly FlowDocument _document;
    private readonly FlowDocumentScrollViewer _viewer;
    private readonly Dictionary<int, Block> _pageStarts = new();
    private readonly CancellationTokenSource _cts = new();
    private double _fontSize = 18;

    public ReadModeWindow(string title, bool dark)
    {
        Title = $"Read Mode — {title}";
        Width = 900;
        Height = 1000;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var background = dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xFB, 0xF8, 0xF1);
        var foreground = dark ? Color.FromRgb(0xE8, 0xE6, 0xE3) : Color.FromRgb(0x22, 0x22, 0x22);

        _document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Georgia"),
            FontSize = _fontSize,
            LineHeight = _fontSize * 1.55,
            PagePadding = new Thickness(48, 32, 48, 48),
            Background = new SolidColorBrush(background),
            Foreground = new SolidColorBrush(foreground),
            ColumnWidth = double.PositiveInfinity,
            MaxPageWidth = 760,
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = true,
        };
        AutomationProperties.SetName(_document, $"{title}, read mode");
        _viewer = new FlowDocumentScrollViewer
        {
            Document = _document,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(background),
            Focusable = true,
        };
        AutomationProperties.SetName(_viewer, "Document text");
        Content = _viewer;
        Background = new SolidColorBrush(background);

        PreviewKeyDown += OnKey;
        Closed += (_, _) => _cts.Cancel();
        Loaded += (_, _) => _viewer.Focus();
    }

    public CancellationToken Cancellation => _cts.Token;

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) { SetFontSize(_fontSize + 2); e.Handled = true; }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { SetFontSize(_fontSize - 2); e.Handled = true; }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0) { SetFontSize(18); e.Handled = true; }
    }

    private void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 10, 48);
        _document.FontSize = _fontSize;
        _document.LineHeight = _fontSize * 1.55;
        foreach (var block in _document.Blocks)
            if (block.Tag is int level && level > 0) block.FontSize = HeadingSize(level);
    }

    private double HeadingSize(int level) => _fontSize * (level switch { 1 => 1.9, 2 => 1.55, 3 => 1.3, 4 => 1.15, _ => 1.05 });

    /// <summary>Appends one page's content (called page by page as extraction proceeds).</summary>
    public void AddPage(PageAccessibleContent page, int pageCount)
    {
        var marker = new Paragraph(new Run($"Page {page.PageNumber} of {pageCount}"))
        {
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, page.PageNumber == 1 ? 0 : 28, 0, 8),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, page.PageNumber == 1 ? 0 : 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
        };
        _document.Blocks.Add(marker);
        _pageStarts[page.PageNumber] = marker;

        if (page.Blocks.Count == 0)
        {
            _document.Blocks.Add(new Paragraph(new Italic(new Run("No text on this page (it may be a scan: Tools → Recognise Text).")))
            { Foreground = Brushes.Gray });
            return;
        }

        List? list = null;
        foreach (var block in page.Blocks)
        {
            if (block.Role == ContentRole.ListItem)
            {
                list ??= new List { MarkerStyle = TextMarkerStyle.None, Padding = new Thickness(16, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
                list.ListItems.Add(new ListItem(Para(block.Text)) { Margin = new Thickness(0, 0, 0, 4) });
                continue;
            }
            if (list != null) { _document.Blocks.Add(list); list = null; }

            Block element = block.Role switch
            {
                _ when block.IsHeading => new Paragraph(new Bold(new Run(block.Text)))
                {
                    FontSize = HeadingSize(block.HeadingLevel),
                    Margin = new Thickness(0, 18, 0, 8),
                    Tag = block.HeadingLevel,
                },
                ContentRole.Figure => new Paragraph(new Italic(new Run(
                    string.IsNullOrWhiteSpace(block.AltText) ? "[Figure]" : $"[Figure: {block.AltText}]"))) { Foreground = Brushes.Gray },
                ContentRole.Caption => new Paragraph(new Italic(new Run(block.Text))) { FontSize = _fontSize * 0.9 },
                ContentRole.Quote => new Paragraph(new Run(block.Text)) { Margin = new Thickness(28, 0, 28, 12), FontStyle = FontStyles.Italic },
                ContentRole.Code => new Paragraph(new Run(block.Text)) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = _fontSize * 0.9 },
                ContentRole.TableHeader => new Paragraph(new Bold(new Run(block.Text))) { Margin = new Thickness(0, 0, 0, 4) },
                ContentRole.TableCell => new Paragraph(new Run(block.Text)) { Margin = new Thickness(0, 0, 0, 4) },
                ContentRole.Note => new Paragraph(new Run(block.Text)) { FontSize = _fontSize * 0.85 },
                _ => Para(block.Text),
            };
            if (block.Language != null)
                element.Language = TryLanguage(block.Language);
            _document.Blocks.Add(element);
        }
        if (list != null) _document.Blocks.Add(list);
    }

    private static Paragraph Para(string text) => new(new Run(text)) { Margin = new Thickness(0, 0, 0, 12) };

    private static System.Windows.Markup.XmlLanguage TryLanguage(string tag)
    {
        try { return System.Windows.Markup.XmlLanguage.GetLanguage(tag); }
        catch (ArgumentException) { return System.Windows.Markup.XmlLanguage.Empty; }
    }

    /// <summary>Scrolls so the given page's content is at the top.</summary>
    public void ShowPage(int pageNumber)
    {
        if (_pageStarts.TryGetValue(pageNumber, out var block))
            block.BringIntoView();
    }

    /// <summary>Fills the window page by page, starting where the reader is, then the rest in order.</summary>
    public async Task FillAsync(Func<int, CancellationToken, Task<PageAccessibleContent>> extract, int pageCount, int currentPage)
    {
        var ct = _cts.Token;
        try
        {
            for (int p = 1; p <= pageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var content = await extract(p, ct);
                AddPage(content, pageCount);
                if (p == currentPage)
                {
                    UpdateLayout();
                    ShowPage(currentPage);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
