using System.Diagnostics;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Interop;
using Jalium.UI.Markup;

namespace LanStartWrite.Inkcanvas;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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

        using var trayIcon = new TrayIconService(
            window,
            window.OpenSettings,
            RestartApplication,
            () => app.Shutdown());

        var exitCode = app.Run();
        AppPreferences.Flush();
        Environment.Exit(exitCode);
    }

    private static void RestartApplication()
    {
        AppPreferences.Flush();
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            Application.Current?.Shutdown();
        }
        catch (Exception exception)
        {
            Trace.WriteLine(exception);
        }
    }

}
