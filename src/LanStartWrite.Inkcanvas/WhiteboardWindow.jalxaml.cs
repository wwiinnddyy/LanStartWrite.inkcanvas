using System.Collections.Generic;
using FluentJalium.Controls;
using Dusk.Ink.Controls;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Automation;
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

    /// <summary>手柄命中比"画出来的那一颗"宽出来的容差（屏幕 DIP）：手指点手柄不能要求点得准。</summary>
    private const double HandleSlopScreen = 4;

    private readonly List<WhiteboardPage> _pages = [];
    private readonly SelectionAdorner _adorner = new();
    private readonly TouchGestureTracker _gestures = new();
    private CanvasSurface _surface = null!;
    private int _activePageIndex;

    /// <summary>捏合的上下限：拉到 0.2 倍看得见整版板书，拉到 8 倍够写最细的字。</summary>
    private const double MinZoom = 0.2;
    private const double MaxZoom = 8;

    private enum Drag
    {
        None,

        /// <summary>按住已选中的那一堆整块挪。</summary>
        Move,

        /// <summary>在空处拖出一个矩形（鼠标与手指）。</summary>
        Marquee,

        /// <summary>在空处画一条闭合轨迹（笔）。</summary>
        Lasso,

        /// <summary>拖某个缩放手柄（编号在 <see cref="_dragHandle"/>）。</summary>
        Scale,

        /// <summary>拖旋转柄。</summary>
        Rotate,
    }

    private Drag _drag;

    /// <summary>
    /// 手指按在空处、<b>还没确定</b>这是框选还是一次捏合的开始。
    /// 确定之前不清旧选择 —— 捏合完发现选择没了，是用户绝对没料到的账。
    /// </summary>
    private bool _pendingEmptyClear;

    private int _dragHandle = -1;
    private int _dragPointerId = -1;
    private Point _pressScreen;
    private Point _lastScreen;
    private readonly List<Point2D> _lassoWorld = [];

    /// <summary>
    /// 选框：<b>选择那一刻</b>从包围盒取一次，之后跟着用户的拖动走。
    /// <para>
    /// 不每帧从 <c>InkSelection.Bounds</c> 反推 —— 那个是"当前墨迹的正立外包盒"，
    /// 一旦允许旋转，它会随转角越算越大（框住的是旋转后的形状，而不是用户圈住的那一块），
    /// 于是第二次拖同一个角就已经不是它了。<see cref="SelectionFrame"/> 的注释里有这段账。
    /// </para>
    /// </summary>
    private SelectionFrame _frame = new(new Point2D(0, 0), 0, 0, 0);

    /// <summary>
    /// 正在做一次<b>会重写点数据</b>的拖动（挪 / 缩 / 转）。
    /// 文档变更处理器靠它分清"自己改的"与"别人改的"：前者只重推几何，后者要让选择作废。
    /// </summary>
    private bool IsTransforming => _drag is Drag.Move or Drag.Scale or Drag.Rotate;

    /// <summary>验收读的几块几何：选框四角、手柄、框选矩形、当前是不是选择态。</summary>
    internal bool IsSelecting => _surface.IsSelectMode;

    internal Rect? AdornerMarquee => _adorner.Marquee;

    internal IReadOnlyList<Point>? AdornerFrameCorners => _adorner.FrameCorners;

    internal IReadOnlyList<Point>? AdornerHandles => _adorner.Handles;

    internal int PageCount => _pages.Count;

    internal int ActivePageIndex => _activePageIndex;

    internal event Action? ActivePageChanged;

    internal event Action? HistoryStateChanged;

    public WhiteboardWindow()
    {
        AllowsTransparency = false;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        InitializeComponent();
        BindPageIcon(PreviousPageButton);
        BindPageIcon(NextPageButton);
        BindPageIcon(AddPageButton);

        // 层级登记：与批注同在画布层。这个类里一行 Topmost 都不该有 ——
        // "画布可见即压过其他应用、但排在批注栏之下"是层自带的性质（见 WindowLayerManager）。
        // 两块画布不会同时在屏，所以同层并存只出现在"其中一块还没建起来"的那段。
        WindowLayerManager.Register(this, WindowLayer.Canvas, "白板");

        var firstPage = CreatePage();
        _surface = firstPage.Surface;
        _surface.AttachTo(InkHost, 0);
        SubscribeSurface(_surface);

        // 选择层在墨迹<b>之上</b>：加在面之后。它不吃命中（IsHitTestVisible=false），
        // 所有输入都还是白板窗口自己按坐标判的。
        InkHost.Children.Add(_adorner);

        PreviousPageButton.Click += (_, _) => ActivateRelativePage(-1);
        NextPageButton.Click += (_, _) => ActivateRelativePage(1);
        AddPageButton.Click += (_, _) => AddPage();

        ApplyBackground();
        UpdatePageControl();
        CanvasOptions.Changed += OnCanvasOptionsChanged;

        // 输入：<b>handledEventsToo = true</b>。引擎在选择态虽然什么都不写，
        // 但它仍然会把指针事件标成已处理（OnPointerDownHandler 末尾那一句），
        // 不带着一句就永远收不到落点 —— 而"收不到"没有任何症状，只是选择不动。
        InkHost.AddHandler(PointerDownEvent, new PointerDownEventHandler(OnPointerDown), true);
        InkHost.AddHandler(PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove), true);
        InkHost.AddHandler(PointerUpEvent, new PointerUpEventHandler(OnPointerUp), true);
        InkHost.AddHandler(PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel), true);

        // Delete 摘掉选中的那些笔迹（整批一步撤销）。只在白板里有意义：批注那块没有"选中"这件事。
        PreviewKeyDown += (_, e) =>
        {
            if (PageControlHost.IsKeyboardFocusWithin) return;
            if (e.Key != Key.Delete || !_surface.IsSelectMode) return;
            e.Handled = DeleteSelection();
        };

        Closed += OnClosed;
    }

    /// <summary>这块画布的墨迹面。工具栏要写的一切（模式、颜色、粗细、擦法、撤销）都从这里走。</summary>
    internal CanvasSurface Surface => _surface;

    private static void BindPageIcon(Button button)
    {
        if (button.Content is IconElement icon) IconInk.Apply(button, icon);
    }

    private WhiteboardPage CreatePage()
    {
        var page = new WhiteboardPage(new CanvasSurface(Dispatcher, assertLoadedSize: false));
        _pages.Add(page);
        return page;
    }

    private void SubscribeSurface(CanvasSurface surface)
    {
        surface.Document.Changed += OnDocumentChanged;
        surface.View.Viewport.Changed += OnViewportChanged;
        surface.HistoryStateChanged += OnSurfaceHistoryStateChanged;
    }

    private void UnsubscribeSurface(CanvasSurface surface)
    {
        surface.Document.Changed -= OnDocumentChanged;
        surface.View.Viewport.Changed -= OnViewportChanged;
        surface.HistoryStateChanged -= OnSurfaceHistoryStateChanged;
    }

    private void AddPage()
    {
        if (_drag != Drag.None || _gestures.IsActive || _surface.History.HasOpenBatch) return;

        CreatePage();
        ActivatePage(_pages.Count - 1);
    }

    private void ActivateRelativePage(int offset) => ActivatePage(_activePageIndex + offset);

    private void ActivatePage(int index)
    {
        if (index < 0 || index >= _pages.Count || index == _activePageIndex) return;
        if (_drag != Drag.None || _gestures.IsActive || _surface.History.HasOpenBatch) return;

        ClearSelection();
        ResetTransientState();

        var previous = _surface;
        previous.DetachFrom(InkHost);
        UnsubscribeSurface(previous);

        _activePageIndex = index;
        _surface = _pages[index].Surface;
        _surface.AttachTo(InkHost, 0);
        SubscribeSurface(_surface);

        ApplyBackground();
        UpdatePageControl();
        ActivePageChanged?.Invoke();
        HistoryStateChanged?.Invoke();
    }

    private void ResetTransientState()
    {
        if (_surface.History.HasOpenBatch) _surface.History.EndBatch();
        _gestures.Reset();
        _drag = Drag.None;
        _dragHandle = -1;
        _dragPointerId = -1;
        _pendingEmptyClear = false;
        _adorner.Marquee = null;
        _adorner.LassoPoints = null;
        _lassoWorld.Clear();
    }

    private void UpdatePageControl()
    {
        var pageNumber = _activePageIndex + 1;
        var pageCount = _pages.Count;
        PageNumberText.Text = $"{pageNumber} / {pageCount}";
        PreviousPageButton.IsEnabled = _activePageIndex > 0;
        NextPageButton.IsEnabled = _activePageIndex < pageCount - 1;
        AutomationProperties.SetName(PreviousPageButton, "上一页");
        AutomationProperties.SetName(PageNumberText, $"第 {pageNumber} 页，共 {pageCount} 页");
        AutomationProperties.SetName(NextPageButton, "下一页");
        AutomationProperties.SetName(AddPageButton, "新建页面");
    }

    private void OnSurfaceHistoryStateChanged() => HistoryStateChanged?.Invoke();

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
        if (IsTransforming)
        {
            // 挪 / 缩 / 转本身就是引擎重写点数据的一串 Modified 变更：
            // 这一路只重推几何，不清选择 —— 认错 kinds 的症状很隐蔽：拖第一帧选择就被自己清掉，
            // 于是选框塌回原点，用户看到框"啪"地跳走（这条是实测 300 DIP 漂移抓出来的）。
            PushFrameGeometry();
            return;
        }

        ClearSelection();
    }

    // ------------------------------------------------------------------ 选择

    /// <summary>取消选择并把选框擦掉。不发通知、不进历史（选择是一份视图，不是文档内容）。</summary>
    internal void ClearSelection()
    {
        if (!_surface.Document.Selection.IsEmpty) _surface.Document.Selection.Clear();
        _frame = new SelectionFrame(new Point2D(0, 0), 0, 0, 0);
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
        if (Local(e) is not { } screen) return;

        // 双指优先：只有手指参与手势（笔态下多指各写各的，是 owner 定的规则，不是漏掉）。
        var id = (int)e.Pointer.PointerId;
        var isTouch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;
        if (isTouch && _gestures.Down(id, screen))
        {
            AbandonDragForGesture();
            return;
        }

        if (_gestures.IsActive || _gestures.IsParked(id) || _drag != Drag.None) return;

        _dragPointerId = id;
        _pressScreen = _lastScreen = screen;
        var world = ToWorld(screen);
        var selection = _surface.Document.Selection;
        _pendingEmptyClear = false;

        // 1) 手柄优先：画出来的那一颗必须就是点得中的那一颗（同一张表、同一个容差）。
        if (!selection.IsEmpty && HandleAt(screen) is { } hit)
        {
            if (hit == SelectionFrame.RotateHandle)
            {
                _drag = Drag.Rotate;
            }
            else
            {
                _drag = Drag.Scale;
                _dragHandle = hit;
            }

            _surface.History.BeginBatch();
            return;
        }

        // 2) 已经有选择，而且按在选框里 → 整块挪。不要求正好点在墨上：
        //    板书里挑的常常是一小片密字，逐笔点中再拖不现实。
        if (!selection.IsEmpty && _frame.IsEmpty == false && ContainsWorld(world))
        {
            BeginMove();
            return;
        }

        // 3) 点在墨迹上 → 换成这一笔，然后按住就能挪。
        var picked = _surface.Document.SelectAt(world, _surface.View.ScreenLengthToWorld(PickToleranceScreen));
        UpdateSelectionGeometry();
        if (!picked.IsEmpty)
        {
            BeginMove();
            return;
        }

        // 4) 空处按下：鼠标与手指拖矩形，笔拖套索。
        // <b>手指那一路不当场清选择</b>：第二指马上落下就是捏合，而那一次"按在空处"根本不该
        // 被当成"取消选择"。所以手指要等到它确实拖出了框（或抬手确认是个点）才清 ——
        // 鼠标与笔不必延迟：它们不会变成双指手势，当场清才是"点空处取消"该有的手感。
        _pendingEmptyClear = isTouch;
        if (!isTouch) ClearSelection();
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

    /// <summary>
    /// 第二指落下：把刚才那半笔单指动作<b>作废</b>。
    /// <para>
    /// 挪动那一路要额外撤掉自己 —— 批还没收口，而 <c>CanUndo</c> 只数已收口的步，
    /// 所以必须先 <c>EndBatch</c>（把这半笔挪动收成一步）再 <c>Undo</c>（原样退回）。
    /// 不这么做的话"想捏合却先拖了一下"会把板书留在挪歪的位置上，而用户从没打算挪它。
    /// </para>
    /// </summary>
    private void AbandonDragForGesture()
    {
        var history = _surface.History;
        var changed = history.OpenBatchChangeCount;

        // <b>只有真的改到了东西才撤</b>：EndBatch 在零变更时不产生一步历史（引擎的语义），
        // 这时再 Undo 就会弹掉<b>上一步真操作</b>。第一指落下、第二指紧跟着落下正是这种零变更
        // —— 症状是"捏一下合少一笔"（而且长得像引擎的历史回放有毛病，实测干净板上 3→3 才排除掉）。
        switch (_drag)
        {
            case Drag.Move:
            case Drag.Scale:
            case Drag.Rotate:
                history.EndBatch();
                if (changed > 0) history.Undo();
                break;
            default:
                if (history.HasOpenBatch) history.EndBatch();
                break;
        }

        _drag = Drag.None;
        _dragHandle = -1;
        _dragPointerId = -1;
        _pendingEmptyClear = false;
        _adorner.Marquee = null;
        _adorner.LassoPoints = null;
        _lassoWorld.Clear();
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
        if (!_surface.IsSelectMode) return;
        if (Local(e) is not { } screen) return;

        var id = (int)e.Pointer.PointerId;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch
            && _gestures.Move(id, screen, out var pan, out var factor, out var anchor))
        {
            // 先平移后缩放：ZoomAt 保证锚点下的世界点不动，而它算的"当前视口"必须是刚平移过的那一个。
            if (pan.X != 0 || pan.Y != 0) _surface.PanByScreen(pan.X, pan.Y);
            if (System.Math.Abs(factor - 1) > 1e-9) _surface.ZoomAt(anchor, factor, MinZoom, MaxZoom);
            return;
        }

        if (_drag == Drag.None || id != _dragPointerId) return;

        var dx = screen.X - _lastScreen.X;
        var dy = screen.Y - _lastScreen.Y;
        var previous = _lastScreen;
        _lastScreen = screen;

        switch (_drag)
        {
            case Drag.Move:
                var delta = ToWorldDelta(dx, dy);
                _surface.Document.Selection.Translate(delta.X, delta.Y);
                _frame.Translate(delta.X, delta.Y);
                PushFrameGeometry();
                break;

            case Drag.Scale:
                // 位移沿<b>选框自己的两根轴</b>折算：转过 90° 之后"往右拖"其实是选框的"往下"。
                var worldDelta = ToWorldDelta(dx, dy);
                var localDelta = _frame.WorldVectorToLocal(worldDelta.X, worldDelta.Y);
                if (_frame.TryScale(_dragHandle, localDelta, RotateOffsetWorld, out var request))
                {
                    ApplyScaleToSelection(request);
                    _frame.ApplyScale(request);
                    PushFrameGeometry();
                }

                break;

            case Drag.Rotate:
                var center = ToScreen(_frame.Center);

                // 极角必须拿<b>上一拍</b>的点算：_lastScreen 在这里已经被上面覆盖成当前点了，
                // 用它的结果就是 before == after、增量恒为 0 —— 症状是"旋转柄拖着完全没反应"，
                // 而中心与半径两条断言都照样绿（它们对"什么都没发生"也成立）。
                var before = System.Math.Atan2(previous.Y - center.Y, previous.X - center.X);
                var after = System.Math.Atan2(screen.Y - center.Y, screen.X - center.X);
                var deltaAngle = after - before;
                if (double.IsFinite(deltaAngle) && System.Math.Abs(deltaAngle) > 1e-9)
                {
                    _surface.Document.Selection.Rotate(_frame.Center.X, _frame.Center.Y, deltaAngle);
                    _frame.ApplyRotation(deltaAngle);
                    PushFrameGeometry();
                }

                break;

            case Drag.Marquee:
                if (_pendingEmptyClear && (System.Math.Abs(screen.X - _pressScreen.X) > ClickSlopScreen
                        || System.Math.Abs(screen.Y - _pressScreen.Y) > ClickSlopScreen))
                {
                    // 拖出了框：这一指确定是框选，此刻才清掉旧选择。
                    _pendingEmptyClear = false;
                    ClearSelection();
                }

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
        _gestures.Reset();
        if (_drag == Drag.None || (int)e.Pointer.PointerId != _dragPointerId) return;

        var id = (int)e.Pointer.PointerId;
        if (_gestures.ContactCount > 0 && e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            // 手势那几根手指的抬起先交还给追踪器：它们不是"选择的收尾"。
            _gestures.Up(id);
        }

        if (_drag == Drag.None || id != _dragPointerId) return;

        var drag = _drag;
        _drag = Drag.None;
        _dragPointerId = -1;

        switch (drag)
        {
            case Drag.Move:
            case Drag.Scale:
            case Drag.Rotate:
                _surface.History.EndBatch();

                // 这里<b>不</b>重算选框：选框已经跟着每帧的意图走完了（挪也平移了、缩也缩了、
                // 转也转了）。从 Bounds 反推会把转角清零 —— 拖完旋转选框就"回正"，
                // 那是用户看得见的错，而 Bounds 本身没算错，只是它表达的是"正立外包盒"。
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
        _gestures.Reset();
        if (_drag == Drag.None || (int)e.Pointer.PointerId != _dragPointerId) return;

        if (_drag is Drag.Move or Drag.Scale or Drag.Rotate) _surface.History.EndBatch();
        _drag = Drag.None;
        _dragHandle = -1;
        _dragPointerId = -1;
        _pendingEmptyClear = false;
        _adorner.Marquee = null;
        _adorner.LassoPoints = null;
        _lassoWorld.Clear();
    }

    /// <summary>
    /// 按<b>当前选择集</b>重算选框（世界系）并刷给选择层。
    /// <para>只在"选择集换了"的那一刻调（点选 / 框选 / 套索 / 取消）：<c>InkSelection.Bounds</c>
    /// 是 O(选中条数)，引擎文档明确警告过别把它当每帧能读的东西；而且拖完之后再用它会把手柄
    /// 位置按"正立外包盒"重置一遍 —— 旋转过的选框会当场回正。</para>
    /// </summary>
    private void UpdateSelectionGeometry()
    {
        var selection = _surface.Document.Selection;
        if (selection.IsEmpty)
        {
            _frame = new SelectionFrame(new Point2D(0, 0), 0, 0, 0);
            _adorner.FrameCorners = null;
            _adorner.Handles = null;
            return;
        }

        _frame = SelectionFrame.FromWorldBounds(selection.Bounds);
        PushFrameGeometry();
    }

    /// <summary>把选框与九个手柄的屏幕位置推给选择层（拖动过程中只重画，不重算选框）。</summary>
    private void PushFrameGeometry()
    {
        // 四条边连成框用的是四个<b>角</b>（0/2/4/6）；另外四颗（边中点）只是手柄。
        var corners = new List<Point>(4)
        {
            ToScreen(_frame.HandleWorld(0, RotateOffsetWorld)),
            ToScreen(_frame.HandleWorld(2, RotateOffsetWorld)),
            ToScreen(_frame.HandleWorld(4, RotateOffsetWorld)),
            ToScreen(_frame.HandleWorld(6, RotateOffsetWorld)),
        };
        _adorner.FrameCorners = corners;

        var handles = new List<Point>(SelectionFrame.HandleCount + 1);
        for (var handle = 0; handle < SelectionFrame.HandleCount; handle++)
        {
            handles.Add(ToScreen(_frame.HandleWorld(handle, RotateOffsetWorld)));
        }

        handles.Add(ToScreen(_frame.HandleWorld(SelectionFrame.RotateHandle, RotateOffsetWorld)));
        _adorner.Handles = handles;
    }

    /// <summary>旋转柄超出顶边那一段：<b>屏幕恒定</b>换算成世界单位（放大之后它不会离选框越来越远）。</summary>
    private double RotateOffsetWorld =>
        SelectionAdorner.RotateHandleOffset / _surface.View.Viewport.Scale;

    /// <summary>世界点 → 这块面上的屏幕点。</summary>
    private Point ToScreen(Point2D world)
    {
        var p = _surface.View.WorldToScreen(world);
        return new Point(p.X, p.Y);
    }

    /// <summary>按在选框里面吗（沿选框自己的两根轴判 —— 转过的选框不能按正立矩形判）。</summary>
    private bool ContainsWorld(Point2D world)
    {
        var local = _frame.WorldToLocal(world);
        return System.Math.Abs(local.X) <= _frame.HalfWidth && System.Math.Abs(local.Y) <= _frame.HalfHeight;
    }

    /// <summary>
    /// 这一落点命中哪颗手柄（编号同 <see cref="SelectionFrame"/> 的那张表），没命中返回 <c>null</c>。
    /// <para>容差取"手柄画出来的半径 + 一截"，且<b>在屏幕空间比</b>：世界空间比的话，
    /// 缩小之后手柄之间的屏幕距离会近到点不准。</para>
    /// </summary>
    private int? HandleAt(Point screen)
    {
        var limit = SelectionAdorner.HandleRadius + HandleSlopScreen;
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var handle = 0; handle <= SelectionFrame.RotateHandle; handle++)
        {
            var center = ToScreen(_frame.HandleWorld(handle, RotateOffsetWorld));
            var distance = System.Math.Max(System.Math.Abs(screen.X - center.X), System.Math.Abs(screen.Y - center.Y));
            if (distance > limit || distance >= bestDistance) continue;
            bestDistance = distance;
            best = handle;
        }

        return best < 0 ? null : best;
    }

    /// <summary>
    /// 把一次缩放意图落到墨迹上。
    /// <para>
    /// 引擎的 <c>Selection.Scale</c> 只认<b>世界轴</b>，而"拖右上角"要的是沿选框自己那两根轴。
    /// 所以非零转角下必须三步：<b>绕中心转平 → 在世界轴上按对角锚点缩放 → 转回去</b>。
    /// 转平之后锚点的世界坐标恰好是 <c>中心 + 对角的局部偏移</c>（推演见 <see cref="SelectionFrame.ScaleRequest"/>）。
    /// 转角为零时这三步退化成中间那一步，不白花两次重写。
    /// </para>
    /// <para>三步都在宿主开的那一批里，所以一次拖拽仍然是一步撤销。</para>
    /// </summary>
    private void ApplyScaleToSelection(SelectionFrame.ScaleRequest request)
    {
        var selection = _surface.Document.Selection;
        var center = _frame.Center;
        var angle = _frame.Angle;
        var tilted = System.Math.Abs(angle) > 1e-9;

        if (tilted) selection.Rotate(center.X, center.Y, -angle);

        var anchor = new Point2D(center.X + request.OppositeLocal.X, center.Y + request.OppositeLocal.Y);
        selection.Scale(anchor.X, anchor.Y, request.ScaleX, request.ScaleY);

        if (tilted) selection.Rotate(center.X, center.Y, angle);
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
        UnsubscribeSurface(_surface);

        // 墨迹控件必须显式拆：Jalium 不代调，而它挂着整棵墨迹视觉树（见 AGENTS）。
        foreach (var page in _pages) page.Surface.Dispose();
        _pages.Clear();
    }
}
