using Dusk.Ink.Primitives;
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
/// <b>分页、缩放漫游与整套"选择 / 框选 / 套索 / 变换"手势不在这里</b>：它们与"底下是纯色还是一张图"
/// 毫无关系，而图片批注要原样用同一套，所以整份搬进了 <see cref="PagedCanvasWindow"/>。
/// 本类只留三件白板自己的事：层级登记名、每页底下铺哪档纯色、以及"新建页面"是加一张空页。
/// </para>
/// <para>
/// 底色来自「画布」设置页（<see cref="CanvasSceneSettings.BackgroundArgb"/>），<b>当场生效</b>：
/// 白板在屏时换一档，那块底立刻就变 —— 这跟穿透 / 冻结那两个"下次进画布才看得出来"的开关
/// 不是一类，所以它不需要一句时机说明，只需要在换的时候真的换。
/// </para>
/// </summary>
public partial class WhiteboardWindow : PagedCanvasWindow
{
    protected override string CanvasLayerName => "白板";

    public WhiteboardWindow()
    {
        AllowsTransparency = false;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        InitializeComponent();

        // 层级登记：与批注同在画布层。这个类里一行 Topmost 都不该有 ——
        // "画布可见即压过其他应用、但排在批注栏之下"是层自带的性质（见 WindowLayerManager）。
        // 两块画布不会同时在屏，所以同层并存只出现在"其中一块还没建起来"的那段。
        WindowLayerManager.Register(this, WindowLayer.Canvas, CanvasLayerName);

        InitializeCanvasHost(
            InkHost,
            PageControlHost,
            PreviousPageButton,
            NextPageButton,
            AddPageButton,
            PageNumberText);
        InitializeSharedCanvas();

        CanvasOptions.Changed += OnCanvasOptionsChanged;
        Closed += (_, _) =>
        {
            CanvasOptions.Changed -= OnCanvasOptionsChanged;
            DisposeSharedCanvas();
        };
    }

    protected override CanvasPage CreatePageCore() =>
        new WhiteboardPage(new CanvasSurface(Dispatcher, assertLoadedSize: false));

    /// <summary>白板的"新建页面"就是<b>加一张空页</b>；图片那边是拉起文件选择框，所以这一条在这里。</summary>
    protected override void OnAddPageRequested() =>
        AddPage(new WhiteboardPage(new CanvasSurface(Dispatcher, assertLoadedSize: false)));

    /// <summary>
    /// 把设置里那一档底色铺到宿主格子上。<b>铺在格子上而不是控件上</b>：
    /// 引擎那块面自己什么都不画（它只铺一块透明底占命中范围），而且它身上不许加变换 ——
    /// 视口矩阵是引擎自己的事，宿主再包一层就把墨迹变换两遍、落点也错位了。
    /// </summary>
    protected override void ApplyPageBackdrops()
    {
        var color = Argb.Unpack(CanvasOptions.For(CanvasScene.Whiteboard).BackgroundArgb);
        var brush = new SolidColorBrush(color);
        InkHost.Background = brush;
        Background = brush;

        // 缩略图那几页也要刷：它们没被激活过，但菜单一打开就要是对的那一档。
        // 而且这一条是"<b>全部</b>页"，不是当前页 —— 换页时基类也会调本方法，两边同一句。
        foreach (var page in Pages)
        {
            page.Thumbnail.PageBackground = color;
            page.Thumbnail.PageBrush = null;
            page.Thumbnail.Refresh();
        }
    }

    private void OnCanvasOptionsChanged(CanvasScene scene)
    {
        if (scene == CanvasScene.Whiteboard) RefreshPageBackdrops();
    }

    /// <summary>
    /// 现在这块底是什么颜色（打包成整数）。<b>验收读它</b>：判的是"格子上真的铺了一块不透明的底"，
    /// 不是"我们调过一次赋值"。
    /// </summary>
    internal uint AppliedBackgroundArgb =>
        InkHost.Background is SolidColorBrush brush ? Argb.Pack(brush.Color) : 0;
}
