using Jalium.UI;
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
        // 与 Jalium.UI.Gallery.Desktop 一致：先初始化 GPU 上下文，避免部分显卡/驱动组合下窗口已创建但不呈现。
        var renderContext = RenderContext.GetOrCreateCurrent(RenderBackend.Auto);
        renderContext.DefaultRenderingEngine = RenderingEngine.Impeller;

        // FluentJalium 装的 dictionaries 是运行时用 XamlReader 解析的，
        // 所以这一步必须排在任何 JALXAML 解析之前 —— 应用自己的页面也是。
        ThemeLoader.Initialize();

        // 窗口之间的层级（画布 < 批注栏 < 二级菜单 < 设置）由 WindowLayerManager 负责，
        // 它挂在各窗口自己的生命周期事件上自己排 —— 主窗口这里只需要正常 Show + Activate，
        // 不必再"先顶一下工具栏"（旧做法与新做法的对照见 WindowLayerManager 的类注释）。
        var app = new Application();

        // FluentJalium（Astra）主题字典 + 应用自有 token。必须在任何控件构造之前。
        AppPreferences.Initialize();
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
