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
        var app = Application.Current;
        if (app == null)
        {
            CurrentTheme = theme;
            ThemeChanged?.Invoke(theme);
            return;
        }

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

            // Only commit the new theme once the resource swap actually succeeded,
            // so CurrentTheme never reports a theme the UI didn't switch to.
            CurrentTheme = theme;
            ThemeChanged?.Invoke(theme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ThemeManager.SetTheme failed to load '{themeUri}': {ex}");
        }
    }

    public static void ToggleTheme()
    {
        SetTheme(CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
    }

    /// <summary>The theme brushes the icons draw with; see <see cref="FollowHighContrast"/>.</summary>
    internal const string IconBrushKey = "IconBrush";
    internal const string IconAccentBrushKey = "IconAccentBrush";

    private static bool _followingHighContrast;

    /// <summary>
    /// While Windows high contrast is on, icons draw in the system's colours instead of the
    /// theme's, so they keep the contrast the user asked for. The overrides go in the
    /// application's own resources, which take precedence over the merged theme dictionary and
    /// so survive a light/dark switch. Applied now, and again whenever the setting changes.
    /// </summary>
    public static void FollowHighContrast()
    {
        ApplyHighContrastIcons();
        if (_followingHighContrast) return;
        _followingHighContrast = true;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyHighContrastIcons));
            }
        };
    }

    internal static void ApplyHighContrastIcons() => ApplyHighContrastIcons(Application.Current?.Resources, SystemParameters.HighContrast);

    internal static void ApplyHighContrastIcons(ResourceDictionary? resources, bool highContrast)
    {
        if (resources == null) return;

        if (highContrast)
        {
            resources[IconBrushKey] = SystemColors.ControlTextBrush;
            resources[IconAccentBrushKey] = SystemColors.HighlightBrush;
        }
        else
        {
            resources.Remove(IconBrushKey);
            resources.Remove(IconAccentBrushKey);
        }
    }
}
