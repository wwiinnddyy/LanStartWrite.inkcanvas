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

        // 保持项目原有窗口生命周期：批注画布 / 工具栏的 Z 序逻辑依赖同一 Application
        // 中由主窗口显式 Show + Activate 后再进入消息循环。
        var app = new Application();

        // FluentJalium（Astra）主题字典 + 应用自有 token。必须在任何控件构造之前。
        AppPreferences.Initialize();
        FluentTheme.Initialize(app);

        var window = new AnnotationToolbarWindow();
        app.MainWindow = window;

        window.Show();
        window.Activate();

        var exitCode = app.Run();
        AppPreferences.Flush();
        Environment.Exit(exitCode);
    }

}
