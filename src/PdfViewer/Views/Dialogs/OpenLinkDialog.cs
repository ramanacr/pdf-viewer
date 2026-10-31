using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using PdfEngine.Vector.Services;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// "Open this link in your browser?": the URL exactly as it will open (normalized), with the site (or
/// mail address) it goes to in bold, so a link whose text says one thing and whose target is
/// another is caught before the browser opens. Cancel is the default button.
/// </summary>
public sealed class OpenLinkDialog : Window
{
    public OpenLinkDialog(Uri uri)
    {
        bool mail = uri.Scheme == Uri.UriSchemeMailto;
        Title = mail ? "Write an email" : "Open link";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SurfaceBgBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = mail ? "Write an email with your mail program?" : "Open this link in your browser?",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var site = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        site.Inlines.Add(new Run(mail ? "To: " : "Site: "));
        site.Inlines.Add(new Bold(new Run(PdfLinkTargets.DisplayHost(uri))));
        AutomationProperties.SetName(site, (mail ? "To: " : "Site: ") + PdfLinkTargets.DisplayHost(uri));
        panel.Children.Add(site);

        var full = new TextBox
        {
            Text = uri.AbsoluteUri, // exactly what opens
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 8),
        };
        AutomationProperties.SetName(full, "Full link address");
        panel.Children.Add(full);

        var note = new TextBlock
        {
            Text = "The link comes from the document. Open it only if you trust where it goes.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = new Button { Content = mail ? "_Write email" : "_Open link", MinWidth = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 0, 12, 0) };
        open.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", MinWidth = 85, Height = 32, IsCancel = true, IsDefault = true };
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => cancel.Focus();
    }
}
