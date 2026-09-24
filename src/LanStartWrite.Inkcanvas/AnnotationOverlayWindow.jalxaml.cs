using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Interop;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 屏幕批注画布：一块<b>透明</b>的全屏墨迹面，盖在当前桌面上。
/// <para>
/// 这块面本身（引擎控件、撤销历史、属性下发、拆销）在 <see cref="CanvasSurface"/> ——
/// 白板那一块用的是同一个类。这个窗口只管屏幕批注特有的三件事：
/// <b>透明</b>、<b>穿透</b>、<b>冻结底图</b>。
/// </para>
/// </summary>
public partial class AnnotationOverlayWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private readonly CanvasSurface _surface;

    /// <summary>穿透的<b>目标状态</b>。外壳可能抹掉样式位，所以这里记着"应该是什么"，
    /// 好在下一次补救时知道往哪边补。</summary>
    private bool _clickThrough;

    private const int WmNcHitTest = 0x0084;

    public AnnotationOverlayWindow()
    {
        AllowsTransparency = true;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        Opacity = 1;
        Background = TransparentBrush;
        InitializeComponent();

        // 层级登记：画布层。这个类里<b>不再出现任何 Topmost 赋值</b> ——
        // "画布要压过其他应用、但要在工具栏之下"由 WindowLayerManager 算，
        // 它还会在画布显形 / 隐藏 / 被激活时自己重排（见那个类的注释）。
        WindowLayerManager.Register(this, WindowLayer.Canvas, "画布");

        _surface = new CanvasSurface(Dispatcher);
        _surface.AttachTo(InkHost);

        // 这块画布的视口<b>必须钉在 1:1</b>。引擎的滚轮缩放与中键漫游是壳里硬开的
        // （没有公开开关），而批注的墨迹讲的是"屏幕上这一块"：视口一动，
        // 字就与它标的那句话错位，冻结底图（按屏幕空间铺的 ImageBrush）也不再对上。
        // 白板那一块反过来 —— 它要的就是能漫游，所以这两句只写在这里。
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
        };
        PreviewMouseDown += (_, e) =>
        {
            // 只截中键。左键那一路不能碰：框架是"鼠标事件未被处理才提升成指针事件"，
            // 在这里标了 Handled 就等于把鼠标书写一起关掉。
            if (e.ChangedButton == MouseButton.Middle) e.Handled = true;
        };

        Closed += OnClosed;
    }

    /// <summary>
    /// 这块画布的墨迹面。<b>本类上不再有一行"转发给面"的包装</b> ——
    /// 那些成员（模式、颜色、粗细、擦法、半径、清空、撤销）在 <see cref="CanvasSurface"/> 上，
    /// 工具栏拿的是 <c>Surface</c>。留一层转发的代价不是多写几行，而是它会变成第二个真相：
    /// 白板那一块没有这层转发，于是"在批注上好使、在白板上没人接"这类断口只会静悄悄出现。
    /// </summary>
    internal CanvasSurface Surface => _surface;

    /// <summary>
    /// 穿透：画布<b>留在屏上</b>，但鼠标与触摸直接落到它下面的窗口上。
    /// <para>
    /// 这里是<b>两只手</b>，缺一不可（用户报过"穿透开着，点下去还是在写字"）：
    /// <list type="number">
    /// <item><b>样式位</b>（<c>WS_EX_TRANSPARENT</c>，见 <see cref="NativeWindowZOrder.SetClickThrough"/>）——
    /// 它只对同一线程的兄弟窗口生效；</item>
    /// <item><b>命中测试钩子</b>（<see cref="PassThroughHook"/>，把 <c>WM_NCHITTEST</c> 顶回
    /// <c>HTTRANSPARENT</c>）—— 跨进程的那一半靠它。</item>
    /// </list>
    /// <b>不能改用 <c>WS_EX_LAYERED</c></b>：这个框架的透明窗口不带它（实测），
    /// 而一个从未设置分层属性的分层窗口<b>根本不显示</b> —— 那时候画布不是穿透，是消失了，
    /// 而且我们的验收会全绿（画布在屏、样式位在、命中跳过，全都成立）。
    /// </para>
    /// </summary>
    internal bool SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        EnsureHitTestHook();
        var applied = ApplyClickThrough();
        Dispatcher.BeginInvoke(() => ApplyClickThrough());
        return applied;
    }

    /// <summary>当前是不是穿透状态。验收读它（回读样式位，不是回读我们设过的属性）。</summary>
    internal bool IsClickThrough => NativeWindowZOrder.HasClickThroughStyle(Handle);

    private bool ApplyClickThrough() => NativeWindowZOrder.SetClickThrough(Handle, _clickThrough);

    private bool _hookInstalled;

    /// <summary>
    /// 命中测试钩子。穿透开着时把 <c>WM_NCHITTEST</c> 顶回 <c>HTTRANSPARENT</c>：
    /// 外壳于是绕开这个窗口、把鼠标交给它下面的那个 —— <b>包括别的应用的窗口</b>。
    /// <para>
    /// 装在第一次开穿透那一刻（那时句柄必然已经在）：装早了没有意义，
    /// 关掉穿透也不必拆 —— 钩子里有 <see cref="_clickThrough"/> 这道闸。
    /// </para>
    /// </summary>
    private void EnsureHitTestHook()
    {
        if (_hookInstalled) return;

        var source = HwndSource.FromHwnd(Handle);
        if (source is null) return;

        source.AddHook(PassThroughHook);
        _hookInstalled = true;
    }

    private IntPtr PassThroughHook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message != WmNcHitTest || !_clickThrough) return IntPtr.Zero;

        handled = true;
        return NativeWindowZOrder.HitTestTransparent;
    }

    /// <summary>
    /// 冻结底图：铺在墨迹<b>下面</b>，让底下那一屏停在截图那一刻。
    /// <para>
    /// 传 <c>null</c> 就是清掉（回到透明，看得见真的桌面）。放在宿主而不是这里生成，
    /// 是因为"什么时候截"是策略（进画布时截一次），"截出来怎么显示"才是这里的事。
    /// </para>
    /// </summary>
    internal void SetFrozenBackground(ImageSource? image)
    {
        InkHost.Background = image is null ? null : new ImageBrush(image);
    }

    /// <summary>当前有没有冻结底图。验收读它。</summary>
    internal bool HasFrozenBackground => InkHost.Background is ImageBrush;

    /// <summary>冻结底图那张图的像素尺寸（没底图时返回 <c>(0, 0)</c>）。</summary>
    internal (int Width, int Height) FrozenBackgroundSize =>
        InkHost.Background is ImageBrush { ImageSource: BitmapSource source }
            ? (source.PixelWidth, source.PixelHeight)
            : (0, 0);

    /// <summary>
    /// 冻结底图折算到 96 DPI 之后的自然尺寸（DIP）。<b>它应当等于画布窗口的尺寸</b> ——
    /// 这是"截图铺上去正好 1:1、没有错位也没有放大"的判据，也是验收读的那个数。
    /// 像素数本身说明不了这件事：那要乘上算出来的 DPI 才知道落到界面上是多大。
    /// </summary>
    internal (double Width, double Height) FrozenBackgroundNaturalSize =>
        InkHost.Background is ImageBrush { ImageSource: BitmapSource source }
            ? (source.PixelWidth * 96.0 / Math.Max(1.0, source.DpiX),
               source.PixelHeight * 96.0 / Math.Max(1.0, source.DpiY))
            : (0, 0);

    private void OnClosed(object? sender, EventArgs e)
    {
        // 墨迹控件必须显式拆：Jalium 不代调，而它挂着整棵墨迹视觉树（见 AGENTS）。
        _surface.Dispose();
    }
}
