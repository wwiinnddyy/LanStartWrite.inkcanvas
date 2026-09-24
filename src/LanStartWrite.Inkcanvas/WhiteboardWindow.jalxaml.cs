using System.Collections.Generic;
using Dusk.Ink.Controls;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
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
/// <para>
/// <b>「选择」这一档的输入归这个窗口</b>：工具栏把引擎的模式设成
/// <see cref="InkEditingMode.None"/>（引擎注释里写明的宿主槽），窗口于是自己挑墨迹、
/// 框选、整块挪动，并把选框与手柄画在 <see cref="SelectionAdorner"/> 上。
/// </para>
/// </summary>
public partial class WhiteboardWindow : Window
{
    /// <summary>点选的容差（<b>屏幕</b> DIP）：手指粗点一下也要挑得中，但大到会挑中隔壁那一笔。</summary>
    private const double PickToleranceScreen = 12;

    /// <summary>按下与抬起差多少算"点了一下"而不是"拖了一个框"（屏幕 DIP）。</summary>
    private const double ClickSlopScreen = 4;

    private readonly CanvasSurface _surface;
    private readonly SelectionAdorner _adorner = new();

    private enum Drag
    {
        None,

        /// <summary>按住已选中的那一堆整块挪。</summary>
        Move,

        /// <summary>在空处拖出一个矩形（鼠标与手指）。</summary>
        Marquee,

        /// <summary>在空处画一条闭合轨迹（笔）。</summary>
        Lasso,
    }

    private Drag _drag;
    private int _dragPointerId = -1;
    private Point _pressScreen;
    private Point _lastScreen;
    private readonly List<Point2D> _lassoWorld = [];

    /// <summary>
    /// 选框：<b>选择那一刻</b>算出来的世界系包围盒。
    /// <para>
    /// 不每帧从 <c>InkSelection.Bounds</c> 反推 —— 那个是"当前墨迹的正立外包盒"，
    /// 一旦允许旋转，它会随转角越算越大（框住旋转后的形状而不是原来那块）。
    /// </para>
    /// </summary>
    private Rect2D _frameWorld;

    /// <summary>验收读的三块几何：选框、框选矩形、当前是不是选择态。</summary>
    internal bool IsSelecting => _surface.IsSelectMode;

    internal Rect? AdornerMarquee => _adorner.Marquee;

    internal IReadOnlyList<Point>? AdornerFrameCorners => _adorner.FrameCorners;

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

        // 选择层在墨迹<b>之上</b>：加在面之后。它不吃命中（IsHitTestVisible=false），
        // 所有输入都还是白板窗口自己按坐标判的。
        InkHost.Children.Add(_adorner);

        ApplyBackground();
        CanvasOptions.Changed += OnCanvasOptionsChanged;
        _surface.Document.Changed += OnDocumentChanged;
        _surface.View.Viewport.Changed += OnViewportChanged;

        // 输入：<b>handledEventsToo = true</b>。引擎在选择态虽然什么都不写，
        // 但它仍然会把指针事件标成已处理（OnPointerDownHandler 末尾那一句），
        // 不带着一句就永远收不到落点 —— 而"收不到"没有任何症状，只是选择不动。
        AddHandler(PointerDownEvent, new PointerDownEventHandler(OnPointerDown), true);
        AddHandler(PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove), true);
        AddHandler(PointerUpEvent, new PointerUpEventHandler(OnPointerUp), true);
        AddHandler(PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel), true);

        // Delete 摘掉选中的那些笔迹（整批一步撤销）。只在白板里有意义：批注那块没有"选中"这件事。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || !_surface.IsSelectMode) return;
            e.Handled = DeleteSelection();
        };

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
        var brush = new SolidColorBrush(Argb.Unpack(CanvasOptions.For(CanvasScene.Whiteboard).BackgroundArgb));
        InkHost.Background = brush;
        Background = brush;
    }

    private void OnCanvasOptionsChanged(CanvasScene scene)
    {
        if (scene == CanvasScene.Whiteboard) ApplyBackground();
    }

    /// <summary>
    /// 视口动了：<b>橡皮半径要重发一次</b>（它是世界单位，而用户手上的刻度是屏幕像素），
    /// 选框则只要重画 —— 它存的是世界系，换算在画的那一步做。
    /// </summary>
    private void OnViewportChanged(object? sender, System.EventArgs e)
    {
        _surface.ReapplyEraserRadius();
        _adorner.InvalidateVisual();
    }

    /// <summary>
    /// 文档变了。<b>引擎没有 SelectionChanged，也不反向清理选择集</b>（被擦掉的编号会一直留在里面），
    /// 所以"选择还新不新"这件事只有宿主自己管得着：不在拖动的当下，一律取消选择。
    /// <para>不这么做会留下"框里是空的、却还能一拖拖走一串不存在的笔迹"，而撤销会撤到别的东西上。</para>
    /// </summary>
    private void OnDocumentChanged(object? sender, Dusk.Ink.Document.InkDocumentChangedEventArgs e)
    {
        if (_drag == Drag.Move)
        {
            // 挪动本身就是一串 Modified 变更：这一路只重画，不清选择。
            _adorner.InvalidateVisual();
            return;
        }

        ClearSelection();
    }

    // ------------------------------------------------------------------ 选择

    /// <summary>取消选择并把选框擦掉。不发通知、不进历史（选择是一份视图，不是文档内容）。</summary>
    internal void ClearSelection()
    {
        if (!_surface.Document.Selection.IsEmpty) _surface.Document.Selection.Clear();
        _frameWorld = Rect2D.Empty;
        _adorner.FrameCorners = null;
        _adorner.Handles = null;
        _adorner.Marquee = null;
        _adorner.LassoPoints = null;
    }

    /// <summary>Esc：先清选择。有选择时返回 true，让工具栏别再往下一步（退出这块画布）。</summary>
    internal bool ClearSelectionForEscape()
    {
        if (_surface.Document.Selection.IsEmpty) return false;
        ClearSelection();
        return true;
    }

    /// <summary>删掉选中的那些笔迹。<b>整批一步撤销</b>（一次选择动作不该被拆成 N 次撤销）。</summary>
    internal bool DeleteSelection()
    {
        var selection = _surface.Document.Selection;
        if (selection.IsEmpty) return false;

        var history = _surface.History;
        bool owns = history.BeginBatch();

        // <b>先把编号抄下来再摘</b>：每一次 Remove 都会发一次文档变更，而本类的变更处理器
        // 会当场清掉选择集 —— 直接枚举 Selection.Ids 就是"边遍历边被改"，
        // 抛的是 Collection was modified（这条是实测撞出来的，不是假想）。
        var ids = selection.Ids.ToArray();
        var removed = 0;
        foreach (var id in ids)
        {
            if (_surface.Document.Remove(id)) removed++;
        }

        if (owns) history.EndBatch();
        ClearSelection();
        return removed > 0;
    }

    /// <summary>当前选中的笔迹条数（验收读它：判的是"挑中了哪几笔"，不是"我们调过一次选择"）。</summary>
    internal int SelectedStrokeCount =>
        _surface.Document.Selection.Ids.Count(id => _surface.Document.Find(id) is not null);

    private Point2D ToWorld(Point screen) => _surface.View.ScreenToWorld(new Point2D(screen.X, screen.Y));

    /// <summary>
    /// 这一批指针点里最后一个的<b>本地坐标</b>（相对那块墨迹面）。
    /// <para>
    /// 取的是 <c>GetIntermediatePoints</c> 的末点而不是某个"当前位置"属性 —— 与引擎喂给内核的
    /// 那一条完全相同（<c>JaliumInkCanvas.ToArgs</c>），两边读到同一个点，选择和落笔才不会错开一格。
    /// 批是空的就返回 <c>null</c>：那种批次没有落点可言，调用方直接跳过这一拍。
    /// </para>
    /// </summary>
    private Point? Local(PointerEventArgs e)
    {
        var points = e.GetIntermediatePoints(_surface.Canvas);
        return points is { Count: > 0 } ? points[points.Count - 1].Position : null;
    }

    /// <summary>屏幕位移 → 世界位移。<b>除以缩放</b>：手指在屏上走 30 DIP，放大两倍时内容只该走 15 世界单位。</summary>
    private Point2D ToWorldDelta(double screenDx, double screenDy)
    {
        var scale = _surface.View.Viewport.Scale;
        return new Point2D(screenDx / scale, screenDy / scale);
    }

    private void OnPointerDown(object sender, PointerDownEventArgs e)
    {
        if (!_surface.IsSelectMode) return;
        if (_drag != Drag.None) return;   // 第二指落下的那一路属于双指手势（见 TouchGestureTracker）

        if (Local(e) is not { } screen) return;

        _dragPointerId = (int)e.Pointer.PointerId;
        _pressScreen = _lastScreen = screen;
        var world = ToWorld(screen);
        var selection = _surface.Document.Selection;

        // 1) 已经有选择，而且按在选框里 → 整块挪。不要求正好点在墨上：
        //    板书里挑的常常是一小片密字，逐笔点中再拖不现实。
        if (!selection.IsEmpty && _frameWorld.Contains(world))
        {
            BeginMove();
            return;
        }

        // 2) 点在墨迹上 → 换成这一笔，然后按住就能挪。
        var picked = _surface.Document.SelectAt(world, _surface.View.ScreenLengthToWorld(PickToleranceScreen));
        UpdateSelectionGeometry();
        if (!picked.IsEmpty)
        {
            BeginMove();
            return;
        }

        // 3) 空处按下：鼠标与手指拖矩形，笔拖套索。
        ClearSelection();
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            _drag = Drag.Lasso;
            _lassoWorld.Clear();
            _lassoWorld.Add(world);
            _adorner.LassoPoints = new List<Point> { screen };
        }
        else
        {
            _drag = Drag.Marquee;
            _adorner.Marquee = new Rect(screen.X, screen.Y, 0, 0);
        }
    }

    private void BeginMove()
    {
        _drag = Drag.Move;

        // 一次拖拽 = 一步撤销：批的所有权在这里拿住，InkSelection.Apply 见到已有批就不会自己收尾
        // （否则拖 20 帧就是 20 步撤销）。
        _surface.History.BeginBatch();
    }

    private void OnPointerMove(object sender, PointerMoveEventArgs e)
    {
        if (_drag == Drag.None || (int)e.Pointer.PointerId != _dragPointerId) return;

        if (Local(e) is not { } screen) return;

        var dx = screen.X - _lastScreen.X;
        var dy = screen.Y - _lastScreen.Y;
        _lastScreen = screen;

        switch (_drag)
        {
            case Drag.Move:
                var delta = ToWorldDelta(dx, dy);
                _surface.Document.Selection.Translate(delta.X, delta.Y);
                // 选框跟着走：它存的是世界系，挪了就整体平移。
                _frameWorld = new Rect2D(
                    _frameWorld.Left + delta.X, _frameWorld.Top + delta.Y,
                    _frameWorld.Right + delta.X, _frameWorld.Bottom + delta.Y);
                _adorner.InvalidateVisual();
                break;

            case Drag.Marquee:
                _adorner.Marquee = new Rect(
                    System.Math.Min(_pressScreen.X, screen.X),
                    System.Math.Min(_pressScreen.Y, screen.Y),
                    System.Math.Abs(screen.X - _pressScreen.X),
                    System.Math.Abs(screen.Y - _pressScreen.Y));
                break;

            case Drag.Lasso:
                _lassoWorld.Add(ToWorld(screen));
                if (_adorner.LassoPoints is List<Point> live)
                {
                    live.Add(screen);

                    // 交进去的就是那个 List，所以追加不会触发属性的 setter —— 得自己说一声重画。
                    _adorner.InvalidateVisual();
                }

                break;
        }
    }

    private void OnPointerUp(object sender, PointerUpEventArgs e)
    {
        if (_drag == Drag.None || (int)e.Pointer.PointerId != _dragPointerId) return;

        if (_drag == Drag.None) return;

        var drag = _drag;
        _drag = Drag.None;
        _dragPointerId = -1;

        switch (drag)
        {
            case Drag.Move:
                _surface.History.EndBatch();
                UpdateSelectionGeometry();
                break;

            case Drag.Marquee:
                var marquee = _adorner.Marquee;
                _adorner.Marquee = null;
                if (marquee is { } box && (box.Width > ClickSlopScreen || box.Height > ClickSlopScreen))
                {
                    // 矩形按<b>世界系</b>交给引擎：拖框时可能已经漫游过，用屏幕矩形直接比会挑错内容。
                    _surface.Document.SelectRect(Rect2D.FromPoints(
                        ToWorld(new Point(box.Left, box.Top)),
                        ToWorld(new Point(box.Right, box.Bottom))));
                }
                else
                {
                    // 点了一下空处：取消选择。这是"把手放下来什么也没圈到"唯一合理的解释。
                    ClearSelection();
                    break;
                }

                UpdateSelectionGeometry();
                break;

            case Drag.Lasso:
                _adorner.LassoPoints = null;
                if (_lassoWorld.Count >= 3)
                {
                    _surface.Document.SelectLasso(_lassoWorld);
                    UpdateSelectionGeometry();
                }
                else
                {
                    ClearSelection();
                }

                _lassoWorld.Clear();
                break;
        }
    }

    private void OnPointerCancel(object sender, PointerCancelEventArgs e)
    {
        if (_drag == Drag.None || (int)e.Pointer.PointerId != _dragPointerId) return;

        if (_drag == Drag.Move) _surface.History.EndBatch();
        _drag = Drag.None;
        _dragPointerId = -1;
        _adorner.Marquee = null;
        _adorner.LassoPoints = null;
        _lassoWorld.Clear();
    }

    /// <summary>
    /// 按<b>当前选择集</b>重算选框（世界系）并刷给选择层。
    /// <para>放在选择变化之后而不是每帧算：<c>InkSelection.Bounds</c> 是 O(选中条数)，
    /// 引擎文档明确警告过别把它当每帧能读的东西。</para>
    /// </summary>
    private void UpdateSelectionGeometry()
    {
        var selection = _surface.Document.Selection;
        if (selection.IsEmpty)
        {
            _frameWorld = Rect2D.Empty;
            _adorner.FrameCorners = null;
            _adorner.Handles = null;
            return;
        }

        _frameWorld = selection.Bounds;
        _adorner.FrameCorners = ScreenFrameCorners(_frameWorld);
        _adorner.Handles = null;   // 八向手柄与旋转柄：下一步
    }

    /// <summary>世界系选框 → 屏幕四角（左上、右上、右下、左下）。旋转那一步会把这一直角换成转过的直角。</summary>
    private IReadOnlyList<Point> ScreenFrameCorners(Rect2D world)
    {
        var view = _surface.View;
        Point Screen(double x, double y)
        {
            var p = view.WorldToScreen(new Point2D(x, y));
            return new Point(p.X, p.Y);
        }

        return
        [
            Screen(world.Left, world.Top),
            Screen(world.Right, world.Top),
            Screen(world.Right, world.Bottom),
            Screen(world.Left, world.Bottom),
        ];
    }

    /// <summary>
    /// 现在这块底是什么颜色（打包成整数）。<b>验收读它</b>：判的是"格子上真的铺了一块不透明的底"，
    /// 不是"我们调过一次赋值"。
    /// </summary>
    internal uint AppliedBackgroundArgb =>
        InkHost.Background is SolidColorBrush brush ? Argb.Pack(brush.Color) : 0;

    private void OnClosed(object? sender, System.EventArgs e)
    {
        CanvasOptions.Changed -= OnCanvasOptionsChanged;
        _surface.Document.Changed -= OnDocumentChanged;
        _surface.View.Viewport.Changed -= OnViewportChanged;

        // 墨迹控件必须显式拆：Jalium 不代调，而它挂着整棵墨迹视觉树（见 AGENTS）。
        _surface.Dispose();
    }
}
