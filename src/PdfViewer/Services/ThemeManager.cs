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

            ApplyControlStyles(app.Resources, theme);

            // Only commit the new theme once the resource swap actually succeeded,
            // so CurrentTheme never reports a theme the UI didn't switch to.
            CurrentTheme = theme;
            FollowTitleBars();
            foreach (Window window in app.Windows)
            {
                ApplyTitleBar(window);
            }
            ThemeChanged?.Invoke(theme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ThemeManager.SetTheme failed to load '{themeUri}': {ex}");
        }
    }

    /// <summary>The user's own switch: changes the theme and remembers it for the next start.</summary>
    public static void ToggleTheme()
    {
        SetTheme(CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
        AppearanceSettings.SaveTheme(CurrentTheme);
    }

    /// <summary>At startup: puts back the theme the user last chose. With no saved choice the light theme stays.</summary>
    public static void RestoreSavedTheme()
    {
        if (AppearanceSettings.Theme is { } saved && saved != CurrentTheme)
        {
            SetTheme(saved);
        }
    }

    /// <summary>
    /// Windows draws parts of its standard controls (drop-downs, combo boxes, the slider, tooltips)
    /// in fixed light colours. The dark theme restyles them from this dictionary, merged last so it
    /// wins over Controls.xaml; the light theme takes it out, so Windows' own look is untouched.
    /// </summary>
    internal const string DarkControlsSource = "Themes/DarkControls.xaml";

    internal static void ApplyControlStyles(ResourceDictionary resources, AppTheme theme)
    {
        var merged = resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Source?.OriginalString.Contains("DarkControls.xaml") == true)
            {
                merged.RemoveAt(i);
            }
        }

        if (theme == AppTheme.Dark)
        {
            merged.Add(new ResourceDictionary { Source = new Uri(DarkControlsSource, UriKind.Relative) });
        }
    }

    private static bool _followingTitleBars;

    /// <summary>Gives every window opened from now on a title bar in the current theme.</summary>
    private static void FollowTitleBars()
    {
        if (_followingTitleBars) return;
        _followingTitleBars = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 from Windows 10 20H1, 19 before it.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Asks Windows for a dark or light title bar to match the theme. Older Windows simply keeps its own.</summary>
    internal static void ApplyTitleBar(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = CurrentTheme == AppTheme.Dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
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
