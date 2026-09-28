using Jalium.UI;
using LanStartWrite.Inkcanvas.Diagnostics;
using Jalium.UI.Controls;
using Jalium.UI.Interop;
using Jalium.UI.Markup;

namespace LanStartWrite.Inkcanvas;

internal static class Program
{
    /// <summary>
    /// 命令行里要开的那份 PDF。<b>先存起来，等批注栏显形之后再开窗。</b>
    /// <para>
    /// 顺序有硬理由：文件关联那条路是"双击一个 .pdf"，此刻已经有一个应用在了，
    /// 用户看到的是**批注栏**先出来、PDF 窗口跟着出来。而 PDF 窗口的批注栏要从
    /// 那棵视觉树上摘下来搬进它（<c>RehostInto</c>）—— 批注栏还没显形时搬，
    /// 摘的是一个没排版的控件，位置与尺寸都是 0，搬完再排版就已经晚了。
    /// </para>
    /// </summary>
    private static string? _pendingPdfPath;

    [STAThread]
    private static void Main(string[] args)
    {
        _pendingPdfPath = FirstPdfArgument(args);

        // ★ 偏好必须**先于**渲染上下文加载。
        // 渲染后端与引擎是存在偏好里的两个字段，而 RenderContext 的后端是**只读**的 ——
        // 它在建出来的那一刻就定死了，之后只能 forceReplace 整个重建。
        // 所以先加载偏好，后端选择才可能真的生效；反过来写，这一整节设置就是摆设。
        // 提前它是安全的：AppPreferences.Initialize 只碰模型层与静态对象（读档、校验、
        // 下发墨迹/笔锋/工具栏的静态状态、订阅那几处静态事件），不构造任何控件。
        AppPreferences.Initialize();

        // 与 Jalium.UI.Gallery.Desktop 一致：先初始化 GPU 上下文，避免部分显卡/驱动组合下窗口已创建但不呈现。
        var preferences = AppPreferences.Current;
        var renderContext = RenderContext.GetOrCreateCurrent(preferences.RenderBackend);
        // 「自动」落成 Impeller —— 那是这一行一直以来的硬编码值。
        // 别把它改成"交给框架挑"：那会让从没动过设置的用户在某次升级后换掉引擎。
        // 解析规则交给 RenderInfo 一处：设置页判断"要不要提示重启"用的是同一个函数，
        // 各写一份的话两份会漂，而漂了的后果是重启提示一直亮着却没有一次是真的。
        renderContext.DefaultRenderingEngine = RenderInfo.ResolveEngine(preferences.RenderingEngine);
        // 记下"这次要的是哪一档、真的落到哪一档、引擎是哪个"：
        // 设置页判断"要不要提示重启"拿落点当基准（不用上下文自报的值 —— 那个读数在帧与帧之间会变），
        // 而"要的"与"落的"不一致时只有这里能说出来，因为框架换失败是不抛异常的。
        RenderInfo.RecordApplied(
            preferences.RenderBackend,
            renderContext.Backend,
            renderContext.DefaultRenderingEngine);

        // 渲染环境写进日志开头。用户报问题时把日志发来，第一行就是环境，
        // 不用先去设置页截图——而"截图"这一步经常就断了。
        RenderInfo.LogOnce();

        // FluentJalium 装的 dictionaries 是运行时用 XamlReader 解析的，
        // 所以这一步必须排在任何 JALXAML 解析之前 —— 应用自己的页面也是。
        ThemeLoader.Initialize();

        // 窗口之间的层级（画布 < 批注栏 < 二级菜单 < 设置）由 WindowLayerManager 负责，
        // 它挂在各窗口自己的生命周期事件上自己排 —— 主窗口这里只需要正常 Show + Activate，
        // 不必再"先顶一下工具栏"（旧做法与新做法的对照见 WindowLayerManager 的类注释）。
        var app = new Application();

        // FluentJalium（Astra）主题字典 + 应用自有 token。必须在任何控件构造之前。
        FluentTheme.Initialize(app);

        var window = new AnnotationToolbarWindow();
        app.MainWindow = window;

        // 出现位置：主屏工作区下方居中、贴着任务栏上方一点。放在 Show 之前，
        // 于是窗口第一次显形就在那儿，不会先闪一下再挪过去。
        // 尺寸读的是构造函数里 FitSizeToContent 量出来的那个数 —— 按钮是数据驱动的，
        // 栏有多宽要到这一刻才知道。
        ToolbarPlacement.Apply(window);

        window.Show();
        window.Activate();

        // 放映联动：**盯着宿主那边有没有在放映**。不接这一句的话，
        // 用户在 PowerPoint 里按 F5 之后本应用什么也不做 ——
        // 不切场景、墨迹不跟、左下角那个页面控件也不出现（三样是同一个原因）。
        // 它必须在批注栏 Show 之后起来：那一刻才有窗口可切。
        using var slideShowWatcher = new SlideShowWatcher(window);
        slideShowWatcher.Start();

        // 排到队列尾：上面那三步（Show / Activate / Apply）都做完之后才开 PDF 窗口，
        // 而它一开就要把批注栏搬进自己身上。
        if (_pendingPdfPath is { } pdf) window.BeginStartupPdf(pdf);

        var exitCode = app.Run();
        AppPreferences.Flush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// 从命令行里挑出第一个存在的 PDF 路径。
    /// <para>
    /// **挑存在的**而不是挑后缀像的：文件关联的注册项有时会带上
    /// <c>"%1"</c> 与一串 <c>/optflags</c>，也常被别的程序塞进不存在的占位路径。
    /// 挑一个开不了的文件会让"双击没反应"，而那比"忽略这个参数"更难查。
    /// </para>
    /// </summary>
    private static string? FirstPdfArgument(string[] args)
    {
        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument)) continue;
            if (argument.StartsWith('-')) continue;
            if (!argument.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(argument);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
