using System.Windows;
using System.Windows.Input;
using Dusk.Ink.Document;
using Dusk.Ink.Primitives;
using FluentJalium.Controls;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 一块墨迹面上的<b>选择与变换</b>：点选 / 框选 / 套索 / 整块挪 / 九颗手柄缩放 / 旋转柄，
/// 加上双指平移与捏合。**与"这一面底下铺的是什么"完全无关。**
/// <para>
/// 它是从 <see cref="PagedCanvasWindow"/> 里<b>抽出来</b>的那一块。原来那份是
/// <c>private</c> 的，而 PDF 需要同一套交互却<b>不能继承那个基类</b>
/// （那边一页挂一块面，翻页是换挂；PDF 是一整份文档一个世界）。所以这里重写成一份
/// 可被任��窗口持有的控制器，而不是让 PDF 去继承。
/// </para>
/// <para>
/// <b><see cref="PagedCanvasWindow"/> 还没有改用这一份</b> —— 它那份 private 副本仍在原地，
/// 它的 471 条断言仍然全绿。改成委托是下一步，而那一步的验收判据是"那份副本被删掉之后
/// 断言还绿"，不是"新副本也能跑"。在那之前<b>这里有两份同源实现</b>，改任何一条规则
/// 都要记得同步另一条。
/// </para>
/// <para>非线程安全：它读写引擎的选择集，而那个对象只能在 UI 线程上动。</para>
/// </summary>
internal sealed class InkSelectionController
{
    private const double PickToleranceScreen = 12;
    private const double ClickSlopScreen = 4;
    private const double HandleSlopScreen = 4;

    private enum Drag
    {
        None,
        Move,
        Marquee,
        Lasso,
        Scale,
        Rotate,
    }

    private readonly CanvasSurface _surface;
    private readonly SelectionAdorner _adorner;
    private readonly TouchGestureTracker _gestures = new();
    private readonly List<Point2D> _lassoWorld = [];

    private SelectionFrame _frame = new(new Point2D(0, 0), 0, 0, 0);
    private Drag _drag;
    private bool _pendingEmptyClear;
    private int _dragHandle = -1;
    private int _dragPointerId = -1;
    private Point _pressScreen;
    private Point _lastScreen;

    internal InkSelectionController(CanvasSurface surface, double minZoom, double maxZoom)
    {
        _surface = surface;
        MinZoom = minZoom;
        MaxZoom = maxZoom;
        _adorner = new SelectionAdorner();
        _surface.Canvas.Document.Changed += OnDocumentChanged;
    }

    internal double MinZoom { get; }

    internal double MaxZoom { get; }

    /// <summary>选择层。<b>加在墨迹面之上</b>，它不吃命中（<c>IsHitTestVisible=false</c>）。</summary>
    internal SelectionAdorner Adorner => _adorner;

    /// <summary>"选择还新不新"变了 —— 视觉要重画。</summary>
    internal event Action? AdornerChanged;

    internal bool IsTransforming => _transforming || _drag is Drag.Move or Drag.Scale or Drag.Rotate;

    internal int SelectedStrokeCount =>
        _surface.Document.Selection.Ids.Count(id => _surface.Document.Find(id) is not null);

    /// <summary>挂上去。<paramref name="host"/> 是那块墨迹面所在的面板。</summary>
    internal void Attach(Panel host)
    {
        host.Children.Add(_adorner);

        // handledEventsToo = true 是**必须的**：引擎在选择态虽然什么都不写，
        // 但它仍会把指针事件标成已处理，不带着这一句就永远收不到落点 ——
        // 而"收不到"没有任何症状，只是选择不动。
        host.AddHandler(UIElement.PointerDownEvent, new PointerDownEventHandler(OnPointerDown), true);
        host.AddHandler(UIElement.PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove), true);
        host.AddHandler(UIElement.PointerUpEvent, new PointerUpEventHandler(OnPointerUp), true);
        host.AddHandler(UIElement.PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel), true);
    }

    internal void Detach(Panel host)
    {
        host.RemoveHandler(UIElement.PointerDownEvent, new PointerDownEventHandler(OnPointerDown));
        host.RemoveHandler(UIElement.PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove));
        host.RemoveHandler(UIElement.PointerUpEvent, new PointerUpEventHandler(OnPointerUp));
        host.RemoveHandler(UIElement.PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel));
        host.Children.Remove(_adorner);
    }

    // ────────────────────────── 选择集 ──────────────────────────

    internal void ClearSelection()
    {
        if (!_surface.Document.Selection.IsEmpty) _surface.Document.Selection.Clear();
        _frame = new SelectionFrame(new Point2D(0, 0), 0, 0, 0);
        _adorner.FrameCorners = null;
        _adorner.Handles = null;
        _adorner.Marquee = null;
        _adorner.LassoPoints = null;
        AdornerChanged?.Invoke();
    }

    /// <summary>Esc：有选择就清掉并返回 true，让工具栏别再往下一步（退出这块画布）。</summary>
    internal bool ClearSelectionForEscape()
    {
        if (_surface.Document.Selection.IsEmpty) return false;
        ClearSelection();
        return true;
    }

    /// <summary>删掉选中的那些笔迹。<b>整批一步撤销</b>（一次选择不该被拆成 N 次撤销）。</summary>
    internal bool DeleteSelection()
    {
        var selection = _surface.Document.Selection;
        if (selection.IsEmpty) return false;

        var history = _surface.History;
        var owns = history.BeginBatch();

        // 先把编号抄下来再摘：每一次 Remove 都会发一次文档变更，而变更处理器会当场清选择集 ——
        // 直接枚举 Selection.Ids 就是"边遍历边被改"，抛的是 Collection was modified（实测撞出来的）。
        var ids = selection.Ids.ToArray();
        var removed = 0;
        foreach (var id in ids)
            if (_surface.Document.Remove(id)) removed++;

        if (owns) history.EndBatch();
        ClearSelection();
        return removed > 0;
    }

    internal void SelectAll()
    {
        _surface.Document.SelectAll();
        UpdateSelectionGeometry();
    }

    /// <summary>
    /// 在<b>选区还成立</b>的前提下做一次成批的变换（挪 / 缩 / 转）。
    /// <para>
    /// 这条是给"不是拖拽、但要改选中内容"的场合用的：它把
    /// <see cref="IsTransforming"/> 打开，所以引擎重写点数据时那一串
    /// <c>Document.Changed</c> 只会重推几何而<b>不会清掉选择集</b>。
    /// </para>
    /// <para>
    /// 不这么做的话症状很隐蔽：直接调 <c>Selection.Translate</c> 会让选择当场消失，
    /// 而 <c>IsTransforming</c> 为假 —— 它只在拖拽期间为真。而"变换之后选择没了"
    /// 与引擎"选择集被外部改动"的语义无法区分，看起来像引擎的毛病。
    /// </para>
    /// </summary>
    internal void TransformSelectionBatch(Action<InkSelection> transform)
    {
        if (_surface.Document.Selection.IsEmpty) return;
        var owns = _surface.History.BeginBatch();
        _transforming = true;
        try
        {
            transform(_surface.Document.Selection);
        }
        finally
        {
            _transforming = false;
            if (owns) _surface.History.EndBatch();
        }

        UpdateSelectionGeometry();
    }

    private bool _transforming;

    /// <summary>
    /// 引擎<b>没有</b> <c>SelectionChanged</c>，也不反向清理选择集（被擦掉的编号一直留在里面），
    /// 所以"选择还新不新"只有宿主管得着：订阅 <c>Document.Changed</c> 一律作废，
    /// 唯独自己正在做的那批变换例外（判据是 <see cref="IsTransforming"/>，挪/缩/转三种都算 ——
    /// 只放过"挪"会让缩放第一帧就把选择自己清掉，报出来是"对角漂了 300 DIP"）。
    /// </summary>
    private void OnDocumentChanged(object? sender, Dusk.Ink.Document.InkDocumentChangedEventArgs e)
    {
        if (IsTransforming) { PushFrameGeometry(); AdornerChanged?.Invoke(); return; }
        if (_surface.Document.Selection.IsEmpty) return;
        ClearSelection();
    }

    // ────────────────────────── 坐标 ──────────────────────────

    private Point2D ToWorld(Point screen) => _surface.View.ScreenToWorld(new Point2D(screen.X, screen.Y));

    private Point ToScreen(Point2D world)
    {
        var p = _surface.View.WorldToScreen(world);
        return new Point(p.X, p.Y);
    }

    /// <summary>这一批指针点里最后一个的本地坐标（与引擎喂给内核的那一条完全相同）。</summary>
    private Point? Local(PointerEventArgs e)
    {
        // 传 _surface.Canvas：坐标要换算到那块墨迹面上，而它才是引擎换算用的那个元素。
        var points = e.GetIntermediatePoints(_surface.Canvas);
        return points is { Count: > 0 } ? points[points.Count - 1].Position : null;
    }

    /// <summary>屏幕位移 → 世界位移。<b>除以缩放</b>。</summary>
    private Point2D ToWorldDelta(double screenDx, double screenDy)
    {
        var scale = _surface.View.Viewport.Scale;
        return new Point2D(screenDx / scale, screenDy / scale);
    }

    /// <summary>旋转柄超出顶边那一段：<b>屏幕恒定</b>换算成世界单位（放大后它不会离选框越来越远）。</summary>
    private double RotateOffsetWorld => SelectionAdorner.RotateHandleOffset / _surface.View.Viewport.Scale;

    // ────────────────────────── 指针 ──────────────────────────

    private void OnPointerDown(object sender, PointerDownEventArgs e)
    {
        if (!_surface.IsSelectMode) return;
        if (Local(e) is not { } screen) return;

        // 双指优先：只有手指参与手势（笔态下多指各写各的，是 owner 定的规则）。
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
            _drag = hit == SelectionFrame.RotateHandle ? Drag.Rotate : Drag.Scale;
            if (_drag == Drag.Scale) _dragHandle = hit;
            _surface.History.BeginBatch();
            return;
        }

        // 2) 已经有选择且按在选框里 → 整块挪。不要求正好点在墨上：
        //    挑的常常是一小片密字，逐笔点中再拖不现实。
        if (!selection.IsEmpty && _frame.IsEmpty == false && ContainsWorld(world))
        {
            BeginMove();
            return;
        }

        // 3) 点在墨迹上 → 换成这一笔，按住就能挪。
        var picked = _surface.Document.SelectAt(world, _surface.View.ScreenLengthToWorld(PickToleranceScreen));
        UpdateSelectionGeometry();
        if (!picked.IsEmpty)
        {
            BeginMove();
            return;
        }

        // 4) 空处按下：鼠标与手指拖矩形，笔拖套索。
        // 手指那一路**不当场清选择**：第二指马上落下就是捏合，那一次"按在空处"不该被当成取消。
        _pendingEmptyClear = isTouch;
        if (!isTouch) ClearSelection();
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            _drag = Drag.Lasso;
            _lassoWorld.Clear();
            _lassoWorld.Add(world);
            _adorner.LassoPoints = [screen];
        }
        else
        {
            _drag = Drag.Marquee;
            _adorner.Marquee = new Rect(screen.X, screen.Y, 0, 0);
        }

        AdornerChanged?.Invoke();
    }

    /// <summary>
    /// 第二指落下：把刚才那半笔单指动作<b>作废</b>。
    /// <para>
    /// 挪动那一路要额外撤掉自己 —— 批还没收口，而 <c>CanUndo</c> 只数已收口的步，
    /// 所以必须先 <c>EndBatch</c>（把这半笔挪动收成一步）再 <c>Undo</c>（原样退回）。
    /// </para>
    /// </summary>
    private void AbandonDragForGesture()
    {
        var history = _surface.History;
        var changed = history.OpenBatchChangeCount;

        // 只有真的改到了东西才撤：EndBatch 在零变更时不产生一步历史，
        // 这时再 Undo 就会弹掉**上一步真操作**（症状："捏一下合少一笔"）。
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
        AdornerChanged?.Invoke();
    }

    /// <summary>一次拖拽 = 一步撤销：批的所有权在这里拿住。</summary>
    private void BeginMove()
    {
        _drag = Drag.Move;
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
            AdornerChanged?.Invoke();
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
                // 位移沿**选框自己的两根轴**折算：转过 90° 之后"往右拖"其实是选框的"往下"。
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
                // 极角必须拿**上一拍**的点算：_lastScreen 在这里已被覆盖成当前点，
                // 用它的结果就是 before == after、增量恒为 0 —— 症状是"旋转柄拖着完全没反应"，
                // 而中心与半径两条断言对"什么都没发生"照样绿。
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
                AdornerChanged?.Invoke();
                break;

            case Drag.Lasso:
                _lassoWorld.Add(ToWorld(screen));
                if (_adorner.LassoPoints is List<Point> live)
                {
                    live.Add(screen);
                    // 交进去的就是那个 List，所以追加不触发 setter —— 得自己说一声重画。
                    _adorner.InvalidateVisual();
                }

                AdornerChanged?.Invoke();
                break;
        }
    }

    private void OnPointerUp(object sender, PointerUpEventArgs e)
    {
        _gestures.Reset();
        if (_drag == Drag.None || (int)e.Pointer.PointerId != _dragPointerId) return;

        var id = (int)e.Pointer.PointerId;
        if (_gestures.ContactCount > 0 && e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            _gestures.Up(id);

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
                // 这里**不**重算选框：它已经跟着每帧的意图走完了。从 Bounds 反推会把转角清零 ——
                // 拖完旋转选框就"回正"，那是用户看得见的错。
                break;

            case Drag.Marquee:
                var marquee = _adorner.Marquee;
                _adorner.Marquee = null;
                if (marquee is { } box && (box.Width > ClickSlopScreen || box.Height > ClickSlopScreen))
                {
                    // 矩形按**世界系**交给引擎：拖框时可能已经漫游过，用屏幕矩形直接比会挑错内容。
                    _surface.Document.SelectRect(Rect2D.FromPoints(
                        ToWorld(new Point(box.Left, box.Top)),
                        ToWorld(new Point(box.Right, box.Bottom))));
                }
                else
                {
                    // 点了一下空处：取消选择。
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

        AdornerChanged?.Invoke();
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
        AdornerChanged?.Invoke();
    }

    // ────────────────────────── 选框几何 ──────────────────────────

    /// <summary>
    /// 按**当前选择集**重算选框（世界系）。
    /// <para>只在"选择集换了"的那一刻调：<c>InkSelection.Bounds</c> 是 O(选中条数)，
    /// 引擎文档明确警告过别把它当每帧能读的东西；而且拖完之后再用它会把手柄位置
    /// 按"正立外包盒"重置一遍 —— 旋转过的选框会当场回正。</para>
    /// </summary>
    private void UpdateSelectionGeometry()
    {
        var selection = _surface.Document.Selection;
        if (selection.IsEmpty)
        {
            _frame = new SelectionFrame(new Point2D(0, 0), 0, 0, 0);
            _adorner.FrameCorners = null;
            _adorner.Handles = null;
            AdornerChanged?.Invoke();
            return;
        }

        _frame = SelectionFrame.FromWorldBounds(selection.Bounds);
        PushFrameGeometry();
        AdornerChanged?.Invoke();
    }

    /// <summary>把选框与九个手柄的屏幕位置推给选择层（拖动过程中只重画，不重算选框）。</summary>
    private void PushFrameGeometry()
    {
        // 四条边连成框用的是四个**角**（0/2/4/6）；另外四颗（边中点）只是手柄。
        _adorner.FrameCorners =
        [
            ToScreen(_frame.HandleWorld(0, RotateOffsetWorld)),
            ToScreen(_frame.HandleWorld(2, RotateOffsetWorld)),
            ToScreen(_frame.HandleWorld(4, RotateOffsetWorld)),
            ToScreen(_frame.HandleWorld(6, RotateOffsetWorld)),
        ];

        var handles = new List<Point>(SelectionFrame.HandleCount + 1);
        for (var handle = 0; handle < SelectionFrame.HandleCount; handle++)
            handles.Add(ToScreen(_frame.HandleWorld(handle, RotateOffsetWorld)));
        handles.Add(ToScreen(_frame.HandleWorld(SelectionFrame.RotateHandle, RotateOffsetWorld)));
        _adorner.Handles = handles;
    }

    /// <summary>按在选框里面吗（沿选框自己的两根轴判 —— 转过的选框不能按正立矩形判）。</summary>
    private bool ContainsWorld(Point2D world)
    {
        var local = _frame.WorldToLocal(world);
        return System.Math.Abs(local.X) <= _frame.HalfWidth && System.Math.Abs(local.Y) <= _frame.HalfHeight;
    }

    /// <summary>
    /// 这一落点命中哪颗手柄，没命中返回 <c>null</c>。容差是"画出来的半径 + 一截"，
    /// 且<b>在屏幕空间比</b>：世界空间比的话缩小之后手柄之间近到点不准。
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
    /// 引擎的 <c>Selection.Scale</c> 只认**世界轴**，而"拖右上角"要的是沿选框自己那两根轴。
    /// 所以非零转角下必须三步：绕中心转平 → 在世界轴上按对角锚点缩放 → 转回去。
    /// 转角为零时三步退化成中间那一步。
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

    /// <summary>视口变了要重画选择层（手柄是屏幕坐标，跟着缩放走）。</summary>
    internal void OnViewportChanged() => PushFrameGeometry();
}
