using FluentJalium.Themes;
using Jalium.UI;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 应用侧的主题入口。控件外观全部交给 FluentJalium（Astra），这里只剩三件事：
/// 把 <see cref="AppPreferences"/> 的档位翻译成 Astra 的主题变体、装 Astra 没有的那几项应用自有
/// token、以及让那几项 token 跟着 Astra 一起换深浅。
/// <para>
/// 换深浅时改的是<b>已发布的那个画笔实例</b>的颜色，不换字典、不换对象 ——
/// 与 Astra 自己同一套做法（它的 <c>RefreshPalette</c> 也是这样把 Light/Dark 拷进现成实例）。
/// 应用标记里 <c>{StaticResource …}</c> 抓的就是对象本身，一旦换成新实例，
/// 已经建好的控件就静默地停在旧色上。
/// </para>
/// </summary>
internal static class FluentTheme
{
    /// <summary>
    /// 应用自有 token 的深色覆盖值。浅色值不在这儿 —— 它就是 <c>Themes/AppTokens.jalxaml</c> 里
    /// 作者写下的那一份，装载时原样捕获一次（见 Initialize），省掉同一个数存两处的机会。
    /// </summary>
    private static readonly (string Key, Color Dark)[] AppDarkTokenColors =
    [
        // 浮动工具栏：实体表面，不用 acrylic/mica（Design.MD §1）。
        ("ToolbarSurfaceBrush", Color.FromRgb(0x2C, 0x2C, 0x2C)),
        // 笔 / 橡皮二级菜单：比工具栏暗一档，与 WinUI 的 flyout 底色对齐但仍是不透明的。
        ("FlyoutSurfaceBrush", Color.FromRgb(0x2C, 0x2C, 0x2C)),
    ];

    private static readonly Dictionary<string, (SolidColorBrush Brush, Color Light, Color Dark)> AppTokens = new();
    private static AppTheme _lastTheme;
    private static bool _lastReduceMotion;

    internal static void Initialize(Application app)
    {
        // 必须在任何应用控件构造之前 —— Astra 在这一步里换排版表，
        // 已经建好的控件不会跟着重解析（见 FluentThemeManager.Apply 的注释）。
        FluentThemeManager.Apply(app, ToVariant(AppPreferences.Current.Theme));
        FluentThemeManager.ReduceMotion = AppPreferences.Current.ReduceMotion;
        _lastTheme = AppPreferences.Current.Theme;
        _lastReduceMotion = AppPreferences.Current.ReduceMotion;

        var palette = Load("AppTokens.jalxaml");
        app.Resources.MergedDictionaries.Add(palette);
        // 库没有的那一项控件样式（九色画笔色板）。
        app.Resources.MergedDictionaries.Add(Load("AppControls.jalxaml"));

        foreach (var (key, dark) in AppDarkTokenColors)
        {
            if (palette[key] is not SolidColorBrush brush)
                throw new InvalidOperationException($"应用 token {key} 不是 SolidColorBrush。");
            // 此刻画笔还是标记里那份浅色值 —— 原样收下，它就是这个 token 的浅色定义。
            AppTokens[key] = (brush, brush.Color, dark);
        }
        ApplyAppTokens();

        // Astra 定深浅与高对比，这两个应用 token 跟着它走一遍。
        FluentThemeManager.Changed += ApplyAppTokens;
        AppPreferences.Changed += OnPreferencesChanged;
    }

    /// <summary>
    /// 偏好里只有主题与减少动画两项动得到主题层，其余（笔粗、橡皮半径……）不该惊动一次全调色板刷新。
    /// </summary>
    /// <summary>取一项应用自有 token 的画笔对象。守卫用它验"翻深浅换的是颜色、不是实例"。</summary>
    internal static Brush AppTokenBrush(string key) => AppTokens[key].Brush;

    private static void OnPreferencesChanged(PreferenceSnapshot value)
    {
        if (value.Theme == _lastTheme && value.ReduceMotion == _lastReduceMotion) return;
        _lastTheme = value.Theme;
        _lastReduceMotion = value.ReduceMotion;
        FluentThemeManager.ApplyTheme(ToVariant(value.Theme));
        FluentThemeManager.ReduceMotion = value.ReduceMotion;
    }

    private static FluentThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Dark => FluentThemeVariant.Dark,
        AppTheme.System => FluentThemeVariant.System,
        _ => FluentThemeVariant.Light,
    };

    private static void ApplyAppTokens()
    {
        foreach (var (brush, light, dark) in AppTokens.Values)
        {
            var target = FluentThemeManager.IsDark ? dark : light;
            brush.Color = target;
        }
    }

    private static ResourceDictionary Load(string name)
    {
        var assembly = typeof(FluentTheme).Assembly;
        var resource = "Themes/" + name;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing app theme resource: {resource}");
        try
        {
            return Jalium.UI.Markup.XamlReader.Load(stream) as ResourceDictionary
                ?? throw new InvalidOperationException($"Not a ResourceDictionary: {resource}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Cannot load app theme resource: {resource}", ex);
        }
    }
}
