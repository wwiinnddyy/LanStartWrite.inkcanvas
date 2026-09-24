using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 白板：一块<b>有底的</b>全屏画布。
/// <para>
/// 与 <see cref="AnnotationOverlayWindow"/> 的差别只在"有没有底"这一件事上，
/// 而这件事把两边的行为分开得很彻底：批注是"盖在别人的画面上"，所以它要能透明、要能穿透、
/// 要把当前那一屏冻住；白板是"这一块画面就是我"，所以它不透明、永远接输入、也没有可截的底。
/// 两块面共用的那部分（引擎控件、撤销历史、属性下发、拆销）在 <see cref="CanvasSurface"/>。
/// </para>
/// <para>
/// 底色来自「画布」设置页（<see cref="CanvasSceneSettings.BackgroundArgb"/>），<b>当场生效</b>：
/// 白板在屏时换一档，那块底立刻就变 —— 这跟穿透 / 冻结那两个"下次进画布才看得出来"的开关
/// 不是一类，所以它不需要一句时机说明，只需要在换的时候真的换。
/// </para>
/// </summary>
public partial class WhiteboardWindow : Window
{
    private readonly CanvasSurface _surface;

    public WhiteboardWindow()
    {
        AllowsTransparency = false;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        InitializeComponent();

        // 层级登记：与批注同在画布层。这个类里一行 Topmost 都不该有 ——
        // "画布可见即压过其他应用、但排在批注栏之下"是层自带的性质（见 WindowLayerManager）。
        // 两块画布不会同时在屏，所以同层并存只出现在"其中一块还没建起来"的那段。
        WindowLayerManager.Register(this, WindowLayer.Canvas, "白板");

        _surface = new CanvasSurface(Dispatcher);
        _surface.AttachTo(InkHost);

        ApplyBackground();
        CanvasOptions.Changed += OnCanvasOptionsChanged;
        Closed += OnClosed;
    }

    /// <summary>这块画布的墨迹面。工具栏要写的一切（模式、颜色、粗细、擦法、撤销）都从这里走。</summary>
    internal CanvasSurface Surface => _surface;

    /// <summary>
    /// 把设置里那一档底色铺到宿主格子上。<b>铺在格子上而不是控件上</b>：
    /// 引擎那块面自己什么都不画（它只铺一块透明底占命中范围），而且它身上不许加变换 ——
    /// 视口矩阵是引擎自己的事，宿主再包一层就把墨迹变换两遍、落点也错位了。
    /// </summary>
    private void ApplyBackground()
    {
        var argb = CanvasOptions.For(CanvasScene.Whiteboard).BackgroundArgb;
        var brush = new SolidColorBrush(Argb.Unpack(argb));
        InkHost.Background = brush;
        Background = brush;
    }

    private void OnCanvasOptionsChanged(CanvasScene scene)
    {
        if (scene != CanvasScene.Whiteboard) return;
        ApplyBackground();
    }

    /// <summary>
    /// 现在这块底是什么颜色（打包成整数）。<b>验收读它</b>：判的是"格子上真的铺了一块不透明的底"，
    /// 不是"我们调过一次赋值"。
    /// </summary>
    internal uint AppliedBackgroundArgb =>
        InkHost.Background is SolidColorBrush brush
            ? Argb.Pack(brush.Color)
            : 0;

    private void OnClosed(object? sender, EventArgs e)
    {
        CanvasOptions.Changed -= OnCanvasOptionsChanged;

        // 墨迹控件必须显式拆：Jalium 不代调，而它挂着整棵墨迹视觉树（见 AGENTS）。
        _surface.Dispose();
    }
}
