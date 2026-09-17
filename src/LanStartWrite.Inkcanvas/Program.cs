using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Interop;

namespace LanStartWrite.Inkcanvas;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 与 Jalium.UI.Gallery.Desktop 一致：先初始化 GPU 上下文，避免部分显卡/驱动组合下窗口已创建但不呈现。
        var renderContext = RenderContext.GetOrCreateCurrent(RenderBackend.Auto);
        renderContext.DefaultRenderingEngine = RenderingEngine.Impeller;

        // 全局主题键（静态）：暗色下 TextPrimary 为白，与浅色圆角批注栏不协调。
        ResourceDictionary.CurrentThemeKey = "Light";

        // 降低 InkCanvas 最小点距（反射），减轻快速书写时的采样丢弃。
        InkCanvasTuning.ApplyStartupDefaults();

        // 保持项目原有窗口生命周期：批注画布 / 工具栏的 Z 序逻辑依赖同一 Application
        // 中由主窗口显式 Show + Activate 后再进入消息循环。
        var app = new Application();

        // 加载项目级 Fluent(WinUI3) 主题字典（token 在前、控件样式在后）。
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
