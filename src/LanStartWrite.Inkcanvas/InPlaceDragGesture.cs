using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 进程内的拖动手势（<b>不走 <c>DragDrop.DoDragDrop</c></b>）。抄的是 Class Island 的
/// <c>AdvancedManagedContextDragBehavior</c> —— 它的类注释第一句就是
/// <c>"This avoids OS drag-drop"</c>。
/// <para>
/// <b>为什么不能直接用 <c>DoDragDrop</c>：</b>它一被调用就进入一个 Win32 嵌套消息循环，
/// 而它是在鼠标<b>按下</b>那一刻被调用的。后果有三条，且每一条都单独够致命：
/// <list type="number">
/// <item>普通点击也变成拖动 —— 阈值不存在，"点一下选中"和"按住拖走"分不开。</item>
/// <item>框架自己的输入管线被重入（那个循环自己在 <c>PeekMessageW</c> + <c>DispatchMessageW</c>），
/// 拖动期间的指针事件由两个循环同时处理。</item>
/// <item>它靠 <c>GetAsyncKeyState</c> 轮询按钮状态，而我们是事件驱动 —— 两套时钟。</item>
/// </list>
/// </para>
/// <para>
/// 进程内这一套就是三件事：<b>按下记起点 → 移动过阈值才开始拖 → 抬手决定落在哪</b>。
/// 拖动期间宿主自己命中测试、自己画落点提示、自己画跟着指针走的预览。
/// </para>
/// <para>
/// 走<b>指针事件</b>（鼠标 / 触摸 / 笔同一条路），不另接触摸 —— Class Island 那份行为也是 PointerPressed/Moved/Released。
/// </para>
/// </summary>
internal sealed class InPlaceDragGesture
{
    /// <summary>
    /// 开始拖动要走过的最小距离（DIP）。<b>必须有</b>：没有它，点一下就是一次拖动，
    /// 于是"选中这一项"永远做不到 —— 而那一项的选中态是这一页另一半功能。
    /// </summary>
    internal const double ThresholdDip = 3;

    private readonly FrameworkElement _source;
    private readonly Action _onPressed;
    private readonly Action _onDragStarted;
    private readonly Action<Point, Point> _onDragMoved;
    private readonly Action<Point> _onDragEnded;
    private readonly Action _onCancelled;

    private Point _pressPoint;
    private bool _armed;
    private bool _dragging;
    private PointerDeviceType _device;

    /// <summary>此刻是不是真的在拖（"只是按下了"不算）。宿主用它决定要不要吞掉这一次点击。</summary>
    internal bool IsDragging => _dragging;

    internal InPlaceDragGesture(
        FrameworkElement source,
        Action onPressed,
        Action onDragStarted,
        Action<Point, Point> onDragMoved,
        Action<Point> onDragEnded,
        Action? onCancelled = null)
    {
        _source = source;
        _onPressed = onPressed;
        _onDragStarted = onDragStarted;
        _onDragMoved = onDragMoved;
        _onDragEnded = onDragEnded;
        _onCancelled = onCancelled ?? (() => { });

        _source.PreviewPointerDown += OnPointerDown;
        _source.PreviewPointerMove += OnPointerMove;
        _source.PreviewPointerUp += OnPointerUp;
        _source.LostMouseCapture += (_, _) => Abort();
    }

    // ---------------------------------------------------------------- 指针（鼠标 / 触摸 / 笔同一条路）

    /// <summary>
    /// 位置一律读 <c>e.Pointer.Position</c>，<b>它是相对"事件被路由到的那一个元素"的</b>。
    /// 本类把处理挂在 <c>_source</c> 上，所以读到的就是相对 <c>_source</c> 的 ——
    /// 与测试合成事件时喂进去的坐标同一个约定，两边不会各算一套。
    /// </summary>
    private void OnPointerDown(object sender, RoutedEventArgs e)
    {
        if (e is not PointerDownEventArgs down) return;

        // 已经有触点在拖就别再进：第二根手指会被当成"另一只手"，拖动在半路改主意。
        if (_armed) { e.Handled = true; return; }
        _device = down.Pointer.PointerDeviceType;
        Arm(down.Pointer.Position, _source);
    }

    private void OnPointerMove(object sender, RoutedEventArgs e)
    {
        if (e is not PointerEventArgs move) return;
        if (!_armed || move.Pointer.PointerDeviceType != _device) return;
        var now = move.Pointer.Position;
        if (!_dragging)
        {
            if (!CrossedThreshold(now)) return;
            Begin();
        }

        Move(now);
    }

    private void OnPointerUp(object sender, RoutedEventArgs e)
    {
        if (e is not PointerUpEventArgs up) return;
        if (!_armed || up.Pointer.PointerDeviceType != _device) return;
        var now = up.Pointer.Position;
        if (_dragging) End(now); else Reset();
    }

    // ---------------------------------------------------------------- 状态机

    private void Arm(Point point, FrameworkElement source)
    {
        _pressPoint = point;
        _armed = true;
        _dragging = false;
        _onPressed();
    }
    private bool CrossedThreshold(Point now) =>
        Math.Abs(now.X - _pressPoint.X) >= ThresholdDip || Math.Abs(now.Y - _pressPoint.Y) >= ThresholdDip;

    private void Begin()
    {
        _dragging = true;
        // 原件压暗：拖动时那一格留在原位不动会让人以为没拖走。
        // <b>没有跟着指针走的幽灵</b>（桌面平台通常是"原件变淡 + 一块半透明副本跟着指针"）：
        // 能承载它的 <c>Window.OverlayLayer</c> 是框架的 internal 成员，应用侧拿不到 ——
        // 而为了一个装饰去反射它，违反本项目"不加反射补丁"那条规矩。
        // 真正要传达的信息是<b>落在哪一格</b>，那一由落点提示线说，不靠幽灵。
        _source.Opacity = 0.35;
        _onDragStarted();
    }

    private void Move(Point now)
    {
        _onDragMoved(_pressPoint, now);
    }

    private void End(Point now)
    {
        var moved = _dragging;
        Reset();
        if (moved) _onDragEnded(now);
    }

    private void Abort()
    {
        if (!_armed) return;
        var was = _dragging;
        Reset();
        if (was) _onCancelled();
    }

    private void Reset()
    {
        if (_dragging) _source.Opacity = 1.0;
        _armed = false;
        _dragging = false;
    }

    internal static Window
    ? FindWindow(DependencyObject? element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Window window) return window;
            current = current is Visual visual ? visual.VisualParent : null;
        }

        return null;
    }
}
