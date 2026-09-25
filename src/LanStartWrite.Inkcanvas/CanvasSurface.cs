using System.Diagnostics;
using Dusk.Adapter.Jalium;
using Dusk.Ink.Canvas;
using Dusk.Ink.Controls;
using Dusk.Ink.Document;
using Dusk.Ink.Model;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 一块<b>墨迹承载面</b>：引擎控件 + 它自己的撤销历史 + "应用状态 → 引擎属性"的那一套下发。
/// <para>
/// 为什么是组合而不是基类窗口：应用里有<b>两块</b>画布（<see cref="CanvasScene"/>：屏幕批注与白板），
/// 它们要的那套东西一模一样，不同的只是窗口形态 —— 一块透明透出桌面、可以穿透、可以冻住底图，
/// 一块是不透明的干净底、能漫游、能把「鼠标」当「选择」用。而 <c>AllowsTransparency</c>
/// 必须在 <c>InitializeComponent</c> 之前就定下来，所以"一个窗口两种形态"这条路迟早变成满地的
/// <c>if (_whiteboard)</c>。抄两份引擎接线更糟：显式 <c>Dispose</c> 纪律、笔锋注入点、
/// 橡皮与粗细的钳位，只要有一处只在一块画布上修，症状就是"这块好使、那块不好使"。
/// </para>
/// <para>
/// <b>这里不放任何工具数据</b>（颜色 / 粗细 / 擦法都在 <see cref="ToolbarTools"/> 的那一项身上），
/// 也不放视口策略 —— 本类只管"把我收到的取值写进引擎，并保证引擎的取值只有一处在被读"。
/// </para>
/// </summary>
internal sealed class CanvasSurface
{
    private readonly Dispatcher _dispatcher;
    private readonly JaliumInkCanvas _canvas = new();
    private readonly EraserPreviewAdorner _eraserPreview = new();
    private readonly InkHistory _history;
    private Panel? _attachedHost;
    private uint? _previewPointerId;
    private readonly bool _assertLoadedSize;
    private PenKind _currentKind = PenKind.Pen;
    private Color _currentColor = Colors.Black;
    private double _currentThickness = 3;
    private EraserMode _eraserMode = EraserMode.Area;

    internal CanvasSurface(Dispatcher dispatcher, bool assertLoadedSize = true)
    {
        _dispatcher = dispatcher;
        _assertLoadedSize = assertLoadedSize;

        // 撤销/重做挂在文档上：书写、擦除、清空、整笔擦、选择变换都进同一条历史。
        _history = new InkHistory(_canvas.Document);
        _canvas.Document.AttachHistory(_history);
        _canvas.Document.Changed += (_, _) => HistoryStateChanged?.Invoke();

        ApplyAttributes();
        ApplyTipOptions();
        InkRuntimeOptions.Changed += OnInkRuntimeOptionsChanged;
        InkTipOptions.Changed += OnInkTipOptionsChanged;
        ApplyRuntimeOptions(InkRuntimeOptions.Current);

#if DEBUG
        if (_assertLoadedSize)
        {
            _canvas.Loaded += (_, _) => _dispatcher.BeginInvoke(() => Debug.Assert(
                _canvas.ActualWidth > 0, "ink host arranged to zero size: nothing will render"));
        }
        _canvas.StrokeCommitted += (_, _) => Debug.WriteLine(
            $"[ink-metrics] doc={_canvas.Document.Count} passes={_canvas.RenderPassCount} "
            + $"last={_canvas.LastInputToRenderMs:F1}ms peak={_canvas.PeakInputToRenderMs:F1}ms");
#endif
    }

    /// <summary>
    /// 引擎控件本身。<b>只给视口、文档这些"引擎自己那份状态"用</b>（漫游、缩放、选择都从这儿进）；
    /// 书写属性一律走本类的方法，不要绕过钳位与单位换算直接改它。
    /// </summary>
    internal JaliumInkCanvas Canvas => _canvas;

    internal InkDocument Document => _canvas.Document;

    internal InkHistory History => _history;

    internal bool EraserPreviewVisible => _eraserPreview.PreviewVisible;

    internal Point? EraserPreviewCenter => _eraserPreview.Center;

    internal double EraserPreviewRadius => _eraserPreview.Radius;

    /// <summary>世界 ↔ 屏幕。白板那一块的漫游、命中半径、橡皮半径换算都读它。</summary>
    internal InkCanvasView View => _canvas.View;

    /// <summary>
    /// 按屏幕位移漫游。<b>转发到引擎的公开入口</b>，与壳里那套中键拖拽走的是同一条门 ——
    /// 所以程序化驱动（双指）与用鼠标中键拖，手感与落点换算完全一致。
    /// </summary>
    internal void PanByScreen(double screenDx, double screenDy) => _canvas.PanByScreen(screenDx, screenDy);

    /// <summary>
    /// 以屏幕点为锚点缩放。<b>锚点必须是手指那一点</b>：传别处的话画面会"跑偏"
    /// （引擎文档里就写着这条）。这里额外带上上下限，是因为壳自己的入口不暴露这两个数。
    /// </summary>
    internal void ZoomAt(Point screenAnchor, double factor, double minScale, double maxScale) =>
        _canvas.View.Viewport.ZoomAt(
            new Point2D(screenAnchor.X, screenAnchor.Y), factor, minScale, maxScale);

    internal void AttachTo(Panel host, int index = -1)
    {
        if (_attachedHost is not null) DetachFrom(_attachedHost);

        var insertIndex = index < 0 || index > host.Children.Count ? host.Children.Count : index;
        host.Children.Insert(insertIndex, _canvas);
        host.Children.Insert(insertIndex + 1, _eraserPreview);
        _attachedHost = host;

        host.AddHandler(UIElement.PointerDownEvent, new PointerDownEventHandler(OnPointerDown), true);
        host.AddHandler(UIElement.PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove), true);
        host.AddHandler(UIElement.PointerUpEvent, new PointerUpEventHandler(OnPointerUp), true);
        host.AddHandler(UIElement.PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel), true);
        _canvas.AddHandler(PointerEvents.PointerExitedEvent, new PointerEventHandler(OnPointerExited), true);
        _canvas.AddHandler(PointerEvents.PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost), true);
    }

    internal void DetachFrom(Panel host)
    {
        host.RemoveHandler(UIElement.PointerDownEvent, new PointerDownEventHandler(OnPointerDown));
        host.RemoveHandler(UIElement.PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove));
        host.RemoveHandler(UIElement.PointerUpEvent, new PointerUpEventHandler(OnPointerUp));
        host.RemoveHandler(UIElement.PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel));
        _canvas.RemoveHandler(PointerEvents.PointerExitedEvent, new PointerEventHandler(OnPointerExited));
        _canvas.RemoveHandler(PointerEvents.PointerCaptureLostEvent, new PointerEventHandler(OnPointerCaptureLost));
        host.Children.Remove(_canvas);
        host.Children.Remove(_eraserPreview);
        if (ReferenceEquals(_attachedHost, host)) _attachedHost = null;
        _previewPointerId = null;
        _eraserPreview.Hide();
    }

    private bool CanShowEraserPreview => !IsSelectMode && _wasErasing;

    private void OnPointerDown(object sender, PointerDownEventArgs e)
    {
        if (!CanShowEraserPreview) return;
        _previewPointerId = e.Pointer.PointerId;
        ShowEraserPreview(e.Pointer);
    }

    private void OnPointerMove(object sender, PointerMoveEventArgs e)
    {
        if (!CanShowEraserPreview) return;
        if (_previewPointerId is { } pointerId && e.Pointer.PointerId != pointerId) return;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse || e.Pointer.IsInContact)
            ShowEraserPreview(e.Pointer);
    }

    private void OnPointerUp(object sender, PointerUpEventArgs e)
    {
        if (_previewPointerId != e.Pointer.PointerId) return;
        _previewPointerId = null;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) _eraserPreview.Hide();
        else ShowEraserPreview(e.Pointer);
    }

    private void OnPointerCancel(object sender, PointerCancelEventArgs e)
    {
        if (_previewPointerId != e.Pointer.PointerId) return;
        _previewPointerId = null;
        _eraserPreview.Hide();
    }

    private void OnPointerExited(object sender, PointerEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) _eraserPreview.Hide();
    }

    private void OnPointerCaptureLost(object sender, PointerEventArgs e)
    {
        _previewPointerId = null;
        _eraserPreview.Hide();
    }

    private void ShowEraserPreview(PointerPoint pointer) =>
        _eraserPreview.Show(pointer.GetPosition(_eraserPreview), CurrentEraserPreviewRadius);

    private double CurrentEraserPreviewRadius => _eraserMode == EraserMode.Area
        ? _eraserScreenRadius
        : Math.Max(4, 10 * _canvas.View.Viewport.Scale);

    // ------------------------------------------------------------- 工具与模式

    /// <summary>
    /// 选择态。<b>开的时候引擎的编辑模式设成 <see cref="InkEditingMode.None"/></b> ——
    /// 那个成员在引擎里的注释就是"不接收墨迹输入（宿主自己在处理，例如双指缩放）"，
    /// 这是它留给宿主的槽，不是一个我们绕过去的手法。
    /// <para>
    /// 关的时候不"恢复成上一个值"，而是按"上一次是写还是擦"这一个事实重发一遍：
    /// 模式只有一个落点（引擎的 <c>EditingMode</c>），而"上一次是什么"记在这里，
    /// 所以不会出现"从选择切回去结果两边都不对"。
    /// </para>
    /// </summary>
    internal bool IsSelectMode { get; private set; }

    /// <summary>上一次非选择态是擦除还是书写 —— 退出选择态时按它重发模式。</summary>
    private bool _wasErasing;

    internal void SetSelectMode(bool enabled)
    {
        if (enabled)
        {
            IsSelectMode = true;
            _previewPointerId = null;
            _eraserPreview.Hide();
            _canvas.EditingMode = InkEditingMode.None;
            return;
        }

        if (!IsSelectMode) return;
        IsSelectMode = false;

        if (_wasErasing) SetEraseMode();
        else SetInkMode();
    }

    internal void SetInkMode()
    {
        IsSelectMode = false;
        _wasErasing = false;
        _canvas.IsEraserMode = false;
        _previewPointerId = null;
        _eraserPreview.Hide();
    }

    internal void SetEraseMode()
    {
        SetEraserMode(_eraserMode);
    }

    /// <summary>换橡皮的擦法：面积擦＝引擎点擦，笔迹擦＝整笔摘除。</summary>
    internal void SetEraserMode(EraserMode mode)
    {
        IsSelectMode = false;
        _eraserMode = mode;
        _wasErasing = true;
        _canvas.EditingMode = mode == EraserMode.Stroke
            ? InkEditingMode.EraseByStroke
            : InkEditingMode.EraseByPoint;
        _eraserPreview.SetRadius(CurrentEraserPreviewRadius);
    }

    /// <summary>
    /// 橡皮半径。<b>参数是屏幕像素，而 <c>Canvas.EraserRadius</c> 要的是世界单位</b> ——
    /// 引擎文档明写"这一步四个适配端目前都没有做"，所以换算在宿主这一侧。
    /// <para>
    /// 批注那块的缩放恒为 1，换算就是乘 1；白板一旦漫游起来，不换算的后果是
    /// "放大两倍之后橡皮盖住的内容也跟着大了一倍"，而这在屏幕上看只是橡皮忽然变笨了。
    /// 钳位仍在像素上做（那把尺与设置页的滑块口径一致），换完再下发。
    /// </para>
    /// </summary>
    internal void SetEraserRadius(double screenRadius)
    {
        _eraserScreenRadius = double.IsFinite(screenRadius) ? Math.Clamp(screenRadius, 4, 48) : 14;
        ReapplyEraserRadius();
    }

    private double _eraserScreenRadius = 14;

    /// <summary>按<b>当前</b>缩放重发一次橡皮半径。漫游那侧在 <c>Viewport.Changed</c> 上调它，
    /// 否则缩放之后橡皮盖住的内容会跟着一起变大（像素不变、世界变了）。</summary>
    internal void ReapplyEraserRadius()
    {
        _canvas.EraserRadius = _canvas.View.ScreenLengthToWorld(_eraserScreenRadius);
        _eraserPreview.SetRadius(CurrentEraserPreviewRadius);
    }

    internal void ClearCanvas() => _canvas.Clear();

    internal void SetPenKind(PenKind kind)
    {
        _currentKind = kind;
        ApplyAttributes();
    }

    internal void SetPenColor(Color color)
    {
        _currentColor = color;
        ApplyAttributes();
    }

    internal void SetPenThickness(double thickness)
    {
        _currentThickness = Math.Max(1, thickness);
        ApplyAttributes();
    }

    private void ApplyAttributes()
    {
        var da = _canvas.InkAttributes;
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

    // ------------------------------------------------------------- 撤销与历史

    internal bool CanUndo => _history.CanUndo;

    internal bool CanRedo => _history.CanRedo;

    internal void Undo() => _history.Undo();

    internal void Redo() => _history.Redo();

    /// <summary>文档变了（因而可撤销/可重做的东西也变了）。宿主工具栏据此刷按钮状态。</summary>
    internal event Action? HistoryStateChanged;

    // --------------------------------------------------- 应用状态 → 引擎属性

    private void OnInkRuntimeOptionsChanged(InkRuntimeSnapshot snapshot)
    {
        _dispatcher.BeginInvoke(() => ApplyRuntimeOptions(snapshot));
    }

    private void ApplyRuntimeOptions(InkRuntimeSnapshot snapshot)
    {
        _canvas.InkAttributes.IgnorePressure = !snapshot.EnablePressure;
    }

    private void OnInkTipOptionsChanged()
    {
        _dispatcher.BeginInvoke(ApplyTipOptions);
    }

    /// <summary>
    /// 笔锋的注入点只有这一处：把应用侧的笔锋状态写进这块面的 <c>TipSettings</c>。
    /// <para>
    /// 走的是引擎的快照往返（按参数名对齐、批量写），因此参数只改一次就只请求一次重绘。
    /// 排队一拍的理由与墨迹偏好一致 —— 变更可能来自别的窗口的输入事件，
    /// 同一拍里改画布属性会让那一拍的渲染读到半套参数。
    /// </para>
    /// </summary>
    private void ApplyTipOptions()
    {
        InkTipOptions.ApplyTo(_canvas.TipSettings);
    }

    /// <summary>
    /// 拆掉这块面。Jalium 不代调、这型别没有终结器，漏一次就把整棵墨迹视觉树留在原地 ——
    /// 而 <see cref="Document"/> 若被外部（未来的白板文件）持着，这份泄漏还会连累它。
    /// <para>调用时机在窗口的 <c>Closed</c> 里，与订阅的解除同拍。</para>
    /// </summary>
    internal void Dispose()
    {
        if (_attachedHost is not null) DetachFrom(_attachedHost);
        InkRuntimeOptions.Changed -= OnInkRuntimeOptionsChanged;
        InkTipOptions.Changed -= OnInkTipOptionsChanged;
        _canvas.Dispose();
    }
}
