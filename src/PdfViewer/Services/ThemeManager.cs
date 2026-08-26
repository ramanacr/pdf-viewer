using System;
using System.Windows;

namespace PdfViewer.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// Manages application-wide Light and Dark theme switching.
/// </summary>
public static class ThemeManager
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;
    public static event Action<AppTheme>? ThemeChanged;

    public static void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        var app = Application.Current;
        if (app == null) return;

        string themeUri = theme == AppTheme.Dark
            ? "Themes/DarkTheme.xaml"
            : "Themes/LightTheme.xaml";

        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(themeUri, UriKind.Relative)
            };

            // Replace existing theme dictionary (index 0 or matched)
            var merged = app.Resources.MergedDictionaries;
            bool replaced = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var source = merged[i].Source?.OriginalString;
                if (source != null && (source.Contains("DarkTheme.xaml") || source.Contains("LightTheme.xaml")))
                {
                    merged[i] = dict;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                merged.Add(dict);
            }

            ThemeChanged?.Invoke(theme);
        }
        catch { }
    }

    public static void ToggleTheme()
    {
        SetTheme(CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
    }
}
