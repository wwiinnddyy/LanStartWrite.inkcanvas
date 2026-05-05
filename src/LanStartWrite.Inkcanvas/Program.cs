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

        var app = new Application();
        var window = new AnnotationToolbarWindow();
        app.MainWindow = window;

        window.Show();
        window.Activate();

        // 仅用无参 Run()：MainWindow 已 Show，内置逻辑会跳过重复 Show，只进入消息循环。
        Environment.Exit(app.Run());
    }
}
