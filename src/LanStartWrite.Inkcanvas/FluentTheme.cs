using System.Collections;
using System.Runtime.CompilerServices;
using Jalium.UI;
using Jalium.UI.Media;
using Jalium.UI.Media.Animation;
using Microsoft.Win32;

namespace LanStartWrite.Inkcanvas;

/// <summary>WinUI resource names, Jalium templates. Brush identities survive theme changes.</summary>
internal static class FluentTheme
{
    private static ResourceDictionary? _palette;
    private static AppTheme? _lastTheme;
    private static bool _lastReduceMotion;
    private static readonly ConditionalWeakTable<UIElement, MotionDuration> Durations = new();
    internal static bool IsDark { get; private set; }
    internal static bool AnimationsEnabled => !AppPreferences.Current.ReduceMotion && SystemParameters.ClientAreaAnimation;
    internal static event Action? Changed;

    internal static void Initialize(Application app)
    {
        _palette = Load("ThemeTokens.Light.jalxaml");
        SynchronizeSystemBrushes();
        app.Resources.MergedDictionaries.Add(_palette);
        foreach (var name in new[]
        {
            "ThemeMetrics.jalxaml", "ThemeType.jalxaml", "ThemeAnimation.jalxaml",
            "Controls/FluentCommon.jalxaml", "Controls/AppBarTools.jalxaml",
            "Controls/FluentInput.jalxaml", "Controls/FluentNavigation.jalxaml",
        })
            app.Resources.MergedDictionaries.Add(Load(name));

        AppPreferences.Changed += value =>
        {
            if (_lastTheme != value.Theme || _lastReduceMotion != value.ReduceMotion)
                ApplyPreferences();
        };
        ApplyPreferences();
    }

    private static ResourceDictionary Load(string name)
    {
        var assembly = typeof(FluentTheme).Assembly;
        var resource = "Themes/Fluent/" + name;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing Fluent theme resource: {resource}");
        try
        {
            return Jalium.UI.Markup.XamlReader.Load(stream) as ResourceDictionary
                ?? throw new InvalidOperationException($"Not a ResourceDictionary: {resource}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot load Fluent theme resource: {resource}", ex);
        }
    }

    internal static Brush Brush(string key) => _palette?[key] as Brush
        ?? throw new InvalidOperationException($"Missing Fluent brush: {key}");

    internal static void ApplyPreferences()
    {
        if (_palette is null) return;
        _lastTheme = AppPreferences.Current.Theme;
        _lastReduceMotion = AppPreferences.Current.ReduceMotion;
        var dark = AppPreferences.Current.Theme switch
        {
            AppTheme.Dark => true,
            AppTheme.System => IsSystemDark(),
            _ => false,
        };
        // Also initialize the framework's theme on the first Light run. Otherwise its
        // native title-bar controls can keep the OS Dark foreground over our Light surface.
        ResourceDictionary.CurrentThemeKey = dark ? "Dark" : "Light";
        if (dark != IsDark)
        {
            var source = Load(dark ? "ThemeTokens.Dark.jalxaml" : "ThemeTokens.Light.jalxaml");
            foreach (DictionaryEntry item in source)
            {
                if (item.Value is SolidColorBrush from && _palette[item.Key] is SolidColorBrush to)
                {
                    to.Color = from.Color;
                    to.Opacity = from.Opacity;
                }
            }
            IsDark = dark;
        }
        SynchronizeSystemBrushes();
        Changed?.Invoke();
        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                ApplyMotionPolicy(window);
                window.InvalidateVisual();
            }
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void SynchronizeSystemBrushes()
    {
        // WinUI's magenta source placeholders are substituted by the platform at runtime.
        // Jalium dictionaries need the same substitution, not literal #FF00FF brushes.
        (string Key, Color Color)[] colors =
        [
            ("SystemColorButtonFaceColorBrush", SystemColors.ControlColor),
            ("SystemColorButtonTextColorBrush", SystemColors.ControlTextColor),
            ("SystemColorGrayTextColorBrush", SystemColors.GrayTextColor),
            ("SystemColorHighlightColorBrush", SystemColors.HighlightColor),
            ("SystemColorHighlightTextColorBrush", SystemColors.HighlightTextColor),
            ("SystemColorHotlightColorBrush", SystemColors.HotTrackColor),
            ("SystemColorWindowColorBrush", SystemColors.WindowColor),
            ("SystemColorWindowTextColorBrush", SystemColors.WindowTextColor),
        ];
        foreach (var (key, color) in colors)
            if (_palette?[key] is SolidColorBrush brush) brush.Color = color;
    }

    internal static void Enter(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, (AnimationTimeline?)null);
        element.Opacity = 1;
        if (!AnimationsEnabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(167)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    internal static void ApplyMotionPolicy(DependencyObject? root)
    {
        if (root is null) return;
        if (root is UIElement element)
        {
            var duration = Durations.GetValue(element, e => new MotionDuration(e.TransitionDuration));
            element.TransitionDuration = AnimationsEnabled ? duration.Value : new Duration(TimeSpan.Zero);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            ApplyMotionPolicy(VisualTreeHelper.GetChild(root, i));
    }

    private sealed record MotionDuration(Duration Value);
}
