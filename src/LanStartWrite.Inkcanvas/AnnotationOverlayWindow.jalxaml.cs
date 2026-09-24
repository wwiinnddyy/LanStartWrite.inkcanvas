using System.Diagnostics;
using Dusk.Adapter.Jalium;
using Dusk.Ink.Controls;
using Dusk.Ink.Document;
using Dusk.Ink.Model;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Interop;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationOverlayWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private readonly JaliumInkCanvas _surface = new();
    private readonly InkHistory _history;
    private PenKind _currentKind = PenKind.Pen;
    private Color _currentColor = Colors.Black;
    private double _currentThickness = 3;
    private EraserMode _eraserMode = EraserMode.Area;

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

        InkHost.Children.Add(_surface);

        // 撤销/重做挂在文档上：书写、擦除、清空、整笔擦都进同一条历史。
        _history = new InkHistory(_surface.Document);
        _surface.Document.AttachHistory(_history);
        _surface.Document.Changed += (_, _) => HistoryStateChanged?.Invoke();

        ApplyAttributes();
        ApplyTipOptions();
        InkRuntimeOptions.Changed += OnInkRuntimeOptionsChanged;
        InkTipOptions.Changed += OnInkTipOptionsChanged;
        ApplyRuntimeOptions(InkRuntimeOptions.Current);
        Closed += OnClosed;

#if DEBUG
        // 可见区由 ArrangeOverride 报进来的尺寸算出；格子塌成零尺寸时画面全空且不报错。
        _surface.Loaded += (_, _) => Debug.Assert(
            _surface.ActualWidth > 0, "ink host arranged to zero size: nothing will render");
        _surface.StrokeCommitted += (_, _) => Debug.WriteLine(
            $"[ink-metrics] doc={_surface.Document.Count} passes={_surface.RenderPassCount} "
            + $"last={_surface.LastInputToRenderMs:F1}ms peak={_surface.PeakInputToRenderMs:F1}ms");
#endif
    }

    public void SetInkMode()
    {
        _surface.IsEraserMode = false;
    }

    public void SetEraseMode()
    {
        SetEraserMode(_eraserMode);
    }

    /// <summary>换橡皮的擦法：面积擦＝引擎点擦，笔迹擦＝整笔摘除。</summary>
    public void SetEraserMode(EraserMode mode)
    {
        _eraserMode = mode;
        _surface.EditingMode = mode == EraserMode.Stroke
            ? InkEditingMode.EraseByStroke
            : InkEditingMode.EraseByPoint;
    }

    public void SetEraserRadius(double radius)
    {
        _surface.EraserRadius = double.IsFinite(radius) ? Math.Clamp(radius, 4, 48) : 14;
    }

    public void ClearCanvas() => _surface.Clear();

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

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public void Undo() => _history.Undo();

    public void Redo() => _history.Redo();

    /// <summary>文档变了（因而可撤销/可重做的东西也变了）。宿主工具栏据此刷按钮状态。</summary>
    public event Action? HistoryStateChanged;

    public void SetPenKind(PenKind kind)
    {
        _currentKind = kind;
        ApplyAttributes();
    }

    public void SetPenColor(Color color)
    {
        _currentColor = color;
        ApplyAttributes();
    }

    public void SetPenThickness(double thickness)
    {
        _currentThickness = Math.Max(1, thickness);
        ApplyAttributes();
    }

    private void ApplyAttributes()
    {
        var da = _surface.InkAttributes;
        da.Kind = KindFor(_currentKind);
        da.Color = new InkColor(
            _currentColor.R,
            _currentColor.G,
            _currentColor.B,
            AlphaFor(_currentKind));
        da.Width = _currentThickness;
        da.Height = _currentThickness;
    }

    private static StrokeKind KindFor(PenKind kind) => kind switch
    {
        PenKind.Highlighter => StrokeKind.Uniform,
        PenKind.Laser => StrokeKind.Laser,
        _ => StrokeKind.VariableWidth,
    };

    private static byte AlphaFor(PenKind kind) => kind switch
    {
        PenKind.Highlighter => InkBrushes.HighlighterAlpha,
        PenKind.Laser => InkBrushes.LaserAlpha,
        _ => byte.MaxValue,
    };

    private void OnInkRuntimeOptionsChanged(InkRuntimeSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => ApplyRuntimeOptions(snapshot));
    }

    private void ApplyRuntimeOptions(InkRuntimeSnapshot snapshot)
    {
        _surface.InkAttributes.IgnorePressure = !snapshot.EnablePressure;
    }

    /// <summary>
    /// 笔锋的注入点只有这一处：把应用侧的笔锋状态写进墨迹控件的 <c>TipSettings</c>。
    /// <para>
    /// 走的是引擎的快照往返（按参数名对齐、批量写），因此参数只改一次就只请求一次重绘。
    /// 排队一拍的理由与墨迹偏好一致 —— 变更可能来自别的窗口的输入事件，
    /// 同一拍里改画布属性会让那一拍的渲染读到半套参数。
    /// </para>
    /// </summary>
    private void OnInkTipOptionsChanged()
    {
        Dispatcher.BeginInvoke(ApplyTipOptions);
    }

    private void ApplyTipOptions()
    {
        InkTipOptions.ApplyTo(_surface.TipSettings);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        InkRuntimeOptions.Changed -= OnInkRuntimeOptionsChanged;
        InkTipOptions.Changed -= OnInkTipOptionsChanged;
        _surface.Dispose();
    }
}
