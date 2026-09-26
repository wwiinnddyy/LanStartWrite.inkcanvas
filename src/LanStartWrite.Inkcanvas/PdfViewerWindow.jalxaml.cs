using System.Diagnostics;
using System.Windows;
using Dusk.Ink.Document;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;
using LanStartWrite.Inkcanvas.Pdf;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// PDF 批注窗口。**第四块画布**，而它与前两块的结构差别是根本的：
/// <para>
/// 白板与图片是"一页一个世界"（<see cref="PagedCanvasWindow"/> 一次只挂一块
/// <see cref="CanvasSurface"/>，翻页就是换挂）。这一块是<b>整份文档一个世界</b>：
/// 一块面、一份 <c>InkDocument</c>、N 页是同一片纸上的 N 个矩形。
/// </para>
/// <para>
/// 于是"翻页"在这里<b>不是换东西，是挪视口</b>（<c>PanByScreen</c>），滚动连续与否
/// 也就不需要一个开关去切换两套机制，只是"挪完之后要不要吸一下"的差别。
/// </para>
/// <para>
/// <b>它是 Window 而不是 CanvasScene 的一个分支</b>：一份 PDF 有自己的文档状态、
/// 自己的渲染档位缓存、自己的最近列表，塞进"场景"那套会把它变成"随时可能被换掉"的东西。
/// 窗口是它的自然形状 —— 开着就开着，关掉才关掉。
/// </para>
/// </summary>
public partial class PdfViewerWindow : Window
{
    private readonly PdfPageCache _cache = new();
    private readonly PdfResolutionPolicy _policy = new();
    private readonly Dictionary<int, PageLayer> _layers = [];
    private readonly List<PageLayer> _layerOrder = [];
    private readonly List<PendingRequest> _pending = [];
    private readonly DispatcherTimer _settleTimer;
    private readonly DispatcherTimer _preloadTimer;

    /// <summary>缩放上下限（与图片/白板同一族）。</summary>
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8;

    private readonly Grid _root = null!;
    private InkSelectionController? _selection;
    private CanvasSurface _surface = null!;
    private Canvas? _worldLayer;
    private PdfDocumentHandle? _document;
    private PdfPageLayout? _layout;
    private int _currentPage;
    private PdfViewerFilmstrip? _filmstrip;
    private Popup? _zoomPopup;
    private ZoomFlyout? _zoomFlyout;

    public PdfViewerWindow()
    {
        AllowsTransparency = false;
        InitializeComponent();
        // Popup 必须挂在窗口根上（独立 HWND 时 PlacementTarget 的坐标系会错），
        // 所以先把根抓在手里，别在每一处重新 cast Content。
        // 标记的根就是一个两列 Grid，cast 失败只可能是标记被改坏了 —— 那时早崩比静默好。
        _root = (Grid)Content!;
        WindowLayerManager.Register(this, WindowLayer.Canvas, "PDF 批注");

        // 沉降定时器只做一件事：到点了问一次策略要不要升档。
        // 它存在的理由是"停稳"没有事件可订阅 —— 没有最后一帧，只有一个"然后什么都没发生"。
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PdfResolutionPolicy.SettleMilliseconds) };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            RefreshVisibleTiers();
        };

        // 预加载走另一个定时器（更慢）：邻近页的低档先行，
        // 所以滚到那儿时已经有图，而不是先空一帧再画。
        _preloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _preloadTimer.Tick += (_, _) =>
        {
            _preloadTimer.Stop();
            PreloadNeighbours();
        };

        InitializeSurface();
        WireChrome();
        Closed += (_, _) => ShutdownPdf();
    }

    /// <summary>打开一份 PDF。<b>失败不抛</b>，返回原因让调用方能念给用户听。</summary>
    internal bool TryOpenPdf(string path, out string? error)
    {
        if (!_cache.TryOpen(path, out var document, out error) || document is null) return false;

        _document = document;
        _policy.Reset();
        BuildLayout(document);
        ResetView();
        _filmstrip?.SetDocument(_layout, path);
        return true;
    }

    /// <summary>批注栏要写的一切都从这块面走（模式、颜色、粗细、擦法、撤销）。</summary>
    internal CanvasSurface Surface => _surface;

    /// <summary>批注栏搬进来的那一格。</summary>
    internal Grid ToolbarHost => (Grid)ToolbarHostGrid;

    /// <summary>撤销/重做可用态的变化 —— 工具栏那两颗钮靠它亮灭。</summary>
    internal event Action? HistoryStateChanged;

    /// <summary>此刻有没有一份打开着的文档（决定要不要占一个窗口）。</summary>
    internal bool HasDocument => _document is not null && _layout is { PageCount: > 0 };

    /// <summary>供验收读：打开的这份文档的页数。</summary>
    internal int PageCount => _document?.PageCount ?? 0;

    /// <summary>供验收读：当前页码（0 基）。</summary>
    internal int CurrentPage => _currentPage;

    /// <summary>供验收读：切页模式开关（连续浏览 = false）。</summary>
    internal bool ContinuousBrowse => AppPreferences.Current.PdfContinuousBrowse;

    /// <summary>供验收读：胶片条此刻在不在这儿（切页模式常驻）。</summary>
    internal bool FilmstripVisible => _filmstrip?.Root.Visibility == Visibility.Visible;

    /// <summary>供验收读：世界层里铺了几块"纸"。</summary>
    internal int WorldLayerChildCount => _worldLayer?.Children.Count ?? 0;

    /// <summary>
    /// 供验收读：世界层是不是压在墨迹面底下。
    /// <para>
    /// 这条是"页图盖住笔迹"的正面钉子，而那个缺陷<b>看起来不像缺陷</b> ——
    /// 照样能写，只是看不见自己写的。所以只能靠层序断言，不能靠"看起来对"。
    /// </para>
    /// </summary>
    internal bool WorldLayerIsBottom => _worldLayer is not null && InkHost.Children.IndexOf(_worldLayer) == 0;

    /// <summary>供验收读：世界原点在屏幕上的 y —— 翻页必须是它动了。</summary>
    internal double WorldOriginScreenY => _surface.View.WorldToScreen(new Point2D(0, 0)).Y;

    /// <summary>供验收读：直接纵向平移（连续模式下不该被吸附）。</summary>
    internal void PanBy(double screenDy) => PanVertical(screenDy);

    private void BuildLayout(PdfDocumentHandle document)
    {
        var sizes = new List<(double, double)>(document.PageCount);
        for (var i = 0; i < document.PageCount; i++)
        {
            var size = document.PageSize(i);
            sizes.Add(size.IsUsable ? (size.WidthPt, size.HeightPt) : (595, 842));
        }

        _layout = new PdfPageLayout(sizes);
        BuildPageLayers(_layout);
    }

    /// <summary>
    /// 按布局给每页建一个 <see cref="PageLayer"/>（图 + 页矩形 + 当前挂着的那一档）。
    /// <para>
    /// <b>每页一个图层，而不是一个图层装 N 张图</b>：每页的分辨率档位是<b>独立</b>的
    /// （第 1 页看清了、第 2 页还在糊，因为用户正在滚第 2 页），所以每页都得能单独换 Source。
    /// 合成一张大图就退化成"整份文档一个档位"，而那正好是连续滚动最不能要的东西。
    /// </para>
    /// </summary>
    private void BuildPageLayers(PdfPageLayout layout)
    {
        ClearPageLayers();
        if (_worldLayer is null) return;

        for (var i = 0; i < layout.PageCount; i++)
        {
            var rect = layout.PageRect(i);
            var image = new Image
            {
                Visibility = Visibility.Hidden,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                Width = rect.Width,
                Height = rect.Height,
            };

            // 页矩形外面包一层 Border 当"纸"：页与页之间的空隙不是画布的一部分，
            // 得有东西把它衬成纸色，否则空隙透出窗口底色，整份文档看着像浮在空中的散页。
            // （`Image` 自己没有 Background —— 它只有画 Source 的那块面。）
            var page = new Border
            {
                Width = rect.Width,
                Height = rect.Height,
                Background = Brushes.White,
                IsHitTestVisible = false,
                Child = image,
            };

            // 图层挂进世界层，而世界层的尺寸/位置由布局摆 —— 于是"页在文档里的位置"
            // 是**布局说了算**，而视口的平移缩放由引擎那份矩阵说了算，两者不重叠。
            Canvas.SetLeft(page, rect.Left);
            Canvas.SetTop(page, rect.Top);
            _worldLayer.Children.Add(page);
            var layer = new PageLayer { Index = i, Image = image, Frame = page };
            _layerOrder.Add(layer);
            _layers[i] = layer;
        }
    }

    private void ClearPageLayers()
    {
        if (_worldLayer is not null)
        {
            foreach (var child in _worldLayer.Children.OfType<Image>()) child.Source = null;
            _worldLayer.Children.Clear();
        }

        _layers.Clear();
        _layerOrder.Clear();
    }

    private void InitializeSurface()
    {
        _surface = new CanvasSurface(Dispatcher, assertLoadedSize: false);
        _surface.AttachTo(InkHost, 0);

        // 世界层**必须在墨迹面底下**（插 0）。顺序反了页图就盖住笔迹 ——
        // 而这一层是整个功能里最容易被后来人"整理"错的地方，所以插 0 后立刻钉住。
        _worldLayer = new Canvas { IsHitTestVisible = false };
        InkHost.Children.Insert(0, _worldLayer);
        ReassertWorldLayerAtBottom();

        // 选择层在墨迹面**之上**：它不吃命中（IsHitTestVisible=false），
        // 所有输入都由 InkSelectionController 按坐标判。
        //
        // **选框是屏幕坐标的，而页图是世界坐标的** —— 两套坐标系在同一个宿主里，
        // 所以缩放/平移时必须让选框跟着重画，否则缩小之后九颗手柄会停在屏幕上不动
        // 而选中的墨迹已经缩走了（症状是"手柄对不上我选的东西"）。
        _selection = new InkSelectionController(_surface, MinZoom, MaxZoom);
        _selection.Attach(InkHost);
        _selection.AdornerChanged += () => { };
        ReassertAdornerOnTop();

        _surface.Canvas.Document.Changed += OnDocumentChanged;
        _surface.HistoryStateChanged += () => HistoryStateChanged?.Invoke();
        _surface.View.Viewport.Changed += OnViewportChanged;
        UpdateLayerTransforms();
    }

    /// <summary>选择层必须在世界层与墨迹面<b>之上</b>，否则页图会把选框盖住。</summary>
    private void ReassertAdornerOnTop()
    {
        if (_selection is null) return;
        if (InkHost.Children.Count > 0 && ReferenceEquals(InkHost.Children[InkHost.Children.Count - 1], _selection.Adorner)) return;
        InkHost.Children.Remove(_selection.Adorner);
        InkHost.Children.Add(_selection.Adorner);
    }

    private void ReassertWorldLayerAtBottom()
    {
        if (_worldLayer is null) return;
        if (InkHost.Children.IndexOf(_worldLayer) == 0) return;
        InkHost.Children.Remove(_worldLayer);
        InkHost.Children.Insert(0, _worldLayer);
    }

    private void WireChrome()
    {
        ((Button)PreviousPageButton!).Click += (_, _) => StepPage(-1);
        ((Button)NextPageButton!).Click += (_, _) => StepPage(1);
        ((Button)ZoomInButton!).Click += (_, _) => StepZoom(+1);
        ((Button)ZoomOutButton!).Click += (_, _) => StepZoom(-1);
        ((Button)ZoomPercentButton!).Click += (_, _) => ToggleZoomMenu();
        ((Button)PageNumberButton!).Click += (_, _) => ScrollToPage(_currentPage);

        // 走局部变量而不是字段：`_zoomFlyout` 是 nullable 字段，而这里刚 new 完不可能是 null。
        // 用局部变量把可空性一次性消掉，比在三处各写一个 `!` 诚实 ——
        // `!` 是"骗编译器别吵"，局部变量是真的编译器知道它非空。
        var flyout = new ZoomFlyout();
        _zoomFlyout = flyout;
        flyout.ZoomChanged += percent =>
        {
            var current = ZoomScale <= 0 ? 1.0 : ZoomScale;
            ZoomBy((percent / 100.0) / current);
        };

        var zoomPopup = new Popup
        {
            PlacementTarget = ZoomControlHost,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = -8,
            IsLightDismissEnabled = true,
            StaysOpen = false,
            ShouldConstrainToRootBounds = true,
            Child = flyout.Root,
        };
        _zoomPopup = zoomPopup;
        // 走 _root 而不是 `((Grid)Content)`：Content 是 object?，每次转都要一次 cast 加一次
        // 可空警告。把根存成字段，那道 cast 只发生在构造时一次（且真的失败了就是白屏，不是静默）。
        _root.Children.Add(zoomPopup);

        // 滚轮 = 连续滚动的**主要**通路，所以它不能只是"转起来什么都发生"。
        // 拦在这里而不是让引擎处理：引擎那份默认是缩放，而 PDF 的主浏览动作是翻页。
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsZoomMenuOpen) { HideZoomMenu(); e.Handled = true; }
            // Esc 的一条固定次序：缩放浮层 → 选区 → 什么都不做。
            // 浮层优先是因为它在最上层，而选区可能本来就是空的（那时 Esc 应当能退出去）。
            if (e.Key == Key.Escape && !IsZoomMenuOpen && _selection?.ClearSelectionForEscape() == true) e.Handled = true;
            if (e.Key == Key.Delete && _surface.IsSelectMode && _selection?.DeleteSelection() == true) e.Handled = true;
            if (e.Key == Key.A && _selection is not null && _surface.IsSelectMode && IsControlDown())
            {
                _selection.SelectAll();
                e.Handled = true;
            }
            if (e.Key == Key.PageDown) { StepPage(1); e.Handled = true; }
            if (e.Key == Key.PageUp) { StepPage(-1); e.Handled = true; }
        };

        // 胶片条只在**切页模式**下常驻：连续浏览时它一直占着左边 128 DIP，
        // 而那一列在"逐页细看"时正是最值钱的地方（并排看两页）。
        _filmstrip = new PdfViewerFilmstrip();
        _filmstrip.PageChosen += index => ScrollToPage(index);
        FilmstripHost.Children.Add(_filmstrip.Root);
        ApplyFilmstripMode();

        AppPreferences.Changed += OnPreferencesChanged;
        Closed += (_, _) => AppPreferences.Changed -= OnPreferencesChanged;
    }

    private void OnPreferencesChanged(PreferenceSnapshot value)
    {
        ApplyFilmstripMode();
        // 换档要立刻重画：吸附与否是"这个窗口的形状"，不是"下次打开才生效"的东西。
        if (!value.PdfContinuousBrowse) SnapToCurrentPage();
    }

    private void ApplyFilmstripMode()
    {
        if (_filmstrip is null) return;
        _filmstrip.Root.Visibility = AppPreferences.Current.PdfContinuousBrowse
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ────────────────────────── 视口与滚动 ──────────────────────────

    private double ZoomScale => _surface.View.Viewport.Scale;

    private int ZoomPercent => (int)Math.Round(ZoomScale * 100);

    private void StepZoom(int direction) => ZoomBy(direction > 0 ? 1.25 : 1.0 / 1.25);

    private void ZoomBy(double factor) => ZoomTo(ZoomScale * factor);

    private void ZoomTo(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) return;
        var target = Math.Clamp(scale, 0.1, 8.0);
        var current = ZoomScale;
        if (current <= 0 || Math.Abs(target - current) < 1e-9) return;
        var centre = new Point(InkHost.ActualWidth / 2, InkHost.ActualHeight / 2);
        _surface.ZoomAt(centre, target / current, 0.1, 8.0);
        SyncZoomUi();
    }

    /// <summary>供验收读：墨迹面排版后的宽（0 = 这扇窗还没显形过）。</summary>
    internal double InkHostActualWidth => InkHost.ActualWidth;

    /// <summary>供验收读：这份文档里有几笔 —— 整份 PDF 共用<b>一份</b>墨迹文档。</summary>
    internal int StrokeCount => _surface.Document.Strokes.Count;

    /// <summary>供验收读：当前选中的笔迹条数。</summary>
    internal int SelectedStrokeCount => _selection?.SelectedStrokeCount ?? 0;

    /// <summary>供验收读：选框现在有几颗手柄（选择态下是 9：八颗 + 一颗旋转柄）。</summary>
    internal int SelectionHandleCount => _selection?.Adorner.Handles?.Count ?? 0;

    /// <summary>供验收读：选框的四条边（点选/框选之后非空）。</summary>
    internal int SelectionFrameCornerCount => _selection?.Adorner.FrameCorners?.Count ?? 0;

    /// <summary>供验收读：选择层压在墨迹面之上（页图不会盖住选框）。</summary>
    internal bool AdornerIsOnTop =>
        _selection is not null
        && InkHost.Children.Count > 0
        && ReferenceEquals(InkHost.Children[InkHost.Children.Count - 1], _selection.Adorner);

    /// <summary>供验收读：胶片条上有几格缩略图已经落地了图。</summary>
    internal int FilmstripFilledCount => _filmstrip?.FilledCount ?? 0;

    /// <summary>点选：按世界坐标挑中那一条笔迹。<b>返回是否选中了</b>（引擎那份返回的是选择集本身）。</summary>
    internal bool SelectStrokeAtWorld(Point world) =>
        !_surface.Document.SelectAt(new Dusk.Ink.Primitives.Point2D(world.X, world.Y), PickToleranceWorld).IsEmpty;

    private const double PickToleranceScreen = 12;

    private double PickToleranceWorld => _surface.View.ScreenLengthToWorld(PickToleranceScreen);

    /// <summary>拖出一个世界矩形做框选。供验收驱动。</summary>
    internal void SelectRectWorld(Rect world)
    {
        _surface.Document.SelectRect(Dusk.Ink.Primitives.Rect2D.FromPoints(
            new Dusk.Ink.Primitives.Point2D(world.Left, world.Top),
            new Dusk.Ink.Primitives.Point2D(world.Right, world.Bottom)));
        _selection?.SelectAll();
    }

    /// <summary>整块挪选中的那些笔迹（世界位移）。供验收驱动。</summary>
    internal void TranslateSelection(double dx, double dy) =>
        _selection?.TransformSelectionBatch(selection => selection.Translate(dx, dy));

    /// <summary>旋转选中的那些笔迹（弧度，绕世界原点）。走控制器，成批一步撤销。</summary>
    internal void RotateSelection(double radians) =>
        _selection?.TransformSelectionBatch(selection => selection.Rotate(0, 0, radians));

    /// <summary>供验收读：能撤销回去几步。</summary>
    internal bool CanUndo => _surface.CanUndo;

    // ────────────────────────── 验收驱动用的口 ──────────────────────────
    // 这些不是"给测试开后门"：它们是把内部动作命名出来，好让验收能问
    // "点了会怎样"而不是"能不能从外面戳进私有状态"。全部 internal，与应用其余部分同一可见性。

    /// <summary>第 <paramref name="index"/> 页在世界里的矩形。</summary>
    internal Rect PageRectWorld(int index) => _layout?.PageRect(index) ?? Rect.Empty;

    /// <summary>直接设缩放（不走按钮）。</summary>
    internal void ZoomToForProbe(double scale) => ZoomTo(scale);

    /// <summary>九颗手柄当前的屏幕 y —— 验收读它就知道选框跟着视口走了没有。</summary>
    internal List<double> SelectionHandleScreenPositions() =>
        _selection?.Adorner.Handles?.Select(static h => h.Y).ToList() ?? [];

    internal bool ClearSelectionForProbe() => _selection?.ClearSelectionForEscape() ?? false;

    internal int FilmstripCardCount => _filmstrip?.CardCount ?? 0;

    internal int FilmstripCurrentPage => _filmstrip?.CurrentPage ?? -1;

    internal void FilmstripChoosePage(int index) => _filmstrip?.ChoosePageForProbe(index);

    /// <summary>
    /// 等胶片缩略图真的落地，最多 3 秒。
    /// <para>
    /// **要等**：缩略图是后台线程光栅化的（实测一档 3–4 ms，但一屏要好几页），
    /// 而验收跑在 UI 线程上。写死 sleep 是不确定的，用"轮询到有图为止"才是确定的 ——
    /// 而且它绿得比 sleep 早，不浪费时间。
    /// </para>
    /// </summary>
    internal bool WaitForFilmstripThumbnails(int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if ((_filmstrip?.FilledCount ?? 0) > 0) return true;
            var frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        return (_filmstrip?.FilledCount ?? 0) > 0;
    }


    private void ToggleZoomMenu()
    {
        if (_zoomPopup is null) return;
        _zoomPopup.IsOpen = !_zoomPopup.IsOpen;
    }

    private void HideZoomMenu()
    {
        if (_zoomPopup is not null) _zoomPopup.IsOpen = false;
    }

    private bool IsZoomMenuOpen => _zoomPopup?.IsOpen == true;

    private static bool IsControlDown() =>
        (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private void SyncZoomUi()
    {
        ((TextBlock)ZoomPercentText!).Text = $"{ZoomPercent}%";
        _zoomFlyout?.ShowScale(ZoomScale);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 纵向滚轮 = 翻页（连续或吸附），Ctrl+滚轮 = 缩放。
        // 这么分是因为"滚轮翻页"与"滚轮缩放"在 PDF 里都有人要，
        // 而同一个滚轮事件不能既翻又缩。缩放放 Ctrl 是读图软件几十年的惯例。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ZoomBy(e.Delta > 0 ? 1.1 : 1.0 / 1.1);
            e.Handled = true;
            return;
        }

        var delta = System.Math.Abs(e.Delta) < 1 ? 0 : e.Delta / 120.0;
        if (delta == 0) return;
        // 一格滚轮走 3/4 屏：整屏会跳过太多内容，而 1/4 屏要多滚三次才翻一页。
        PanVertical(-delta * InkHost.ActualHeight * 0.75);
        e.Handled = true;
    }

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        SyncZoomUi();
        UpdateLayerTransforms();
        // 选框是屏幕坐标的：视口一变，页图走了而选框必须跟着走。
        _selection?.OnViewportChanged();
        ReassertAdornerOnTop();
        NoteViewport();
    }

    /// <summary>
    /// 记录一次视口移动，喂给分辨率策略，必要时起那两个定时器。
    /// <para>
    /// 这里是<b>唯一</b>把"用户在动"告诉策略的地方。滚轮、拖动、缩放、跳转页都走它，
    /// 所以那两条规则（移动中只给低档、停稳才升档）对所有输入方式一视同仁 ——
    /// 分开做的话，缩放那条路会漏掉，于是"缩放时疯狂重光栅化"。
    /// </para>
    /// </summary>
    private void NoteViewport()
    {
        if (_layout is null) return;
        var centreScreenY = InkHost.ActualHeight / 2;
        _policy.NoteViewportMoved(centreScreenY, centreScreenY);
        UpdateCurrentPage();
        _settleTimer.Stop();
        _settleTimer.Start();
        _preloadTimer.Stop();
        _preloadTimer.Start();
    }

    private void PanVertical(double screenDy)
    {
        _surface.PanByScreen(0, screenDy);
    }

    private void StepPage(int direction) => ScrollToPage(_currentPage + direction);

    /// <summary>把视口挪到某一页的顶边 —— 这就是这里的"翻页"。</summary>
    internal void ScrollToPage(int index)
    {
        if (_layout is null) return;
        var clamped = Math.Clamp(index, 0, _layout.PageCount - 1);
        var targetTop = _layout.PageTop(clamped);

        // 世界 y → 屏幕位移：视口要上移到该页顶边，需要的位移就是"当前世界原点离目标多远"。
        var origin = _surface.View.WorldToScreen(new Point2D(0, targetTop));
        PanVertical(-origin.Y);
    }

    private void SnapToCurrentPage()
    {
        if (_layout is null || !AppPreferences.Current.PdfContinuousBrowse) ScrollToPage(_currentPage);
    }

    private void UpdateCurrentPage()
    {
        if (_layout is null) return;
        var origin = _surface.View.WorldToScreen(new Point2D(0, 0));
        var centreWorldY = -origin.Y / Math.Max(ZoomScale, 0.0001) + InkHost.ActualHeight / 2 / Math.Max(ZoomScale, 0.0001);
        var page = _layout.NearestPageTo(centreWorldY);
        if (page < 0 || page == _currentPage) { UpdatePageLabel(); return; }

        _currentPage = page;
        _cache.SetCurrentPage(page);
        UpdatePageLabel();
        _filmstrip?.SetCurrentPage(page);
    }

    private void UpdatePageLabel()
    {
        ((TextBlock)PageNumberText!).Text = _layout is null || _layout.PageCount == 0
            ? "0 / 0"
            : $"{_currentPage + 1} / {_layout.PageCount}";
    }

    // ────────────────────────── 分辨率档位 ──────────────────────────

    private void RefreshVisibleTiers()
    {
        if (_layout is null) return;
        var zoom = ZoomScale;
        foreach (var layer in _layerOrder)
        {
            var tier = _policy.DesiredTier(zoom);
            if (layer.Tier == tier && layer.Image.Source is not null) continue;
            // 视口里的页：立刻要那一档。
            if (IsPageVisible(layer.Index))
            {
                EnsureTier(layer.Index, tier);
            }
        }

        foreach (var layer in _layerOrder)
        {
            var tier = _policy.DesiredTier(zoom);
            if (IsPageVisible(layer.Index) && layer.Tier == tier) _policy.Publish(tier);
        }
    }

    private bool IsPageVisible(int index)
    {
        if (_layout is null) return false;
        var rect = _layout.PageRect(index);
        var top = _surface.View.WorldToScreen(new Point2D(0, rect.Top)).Y;
        var bottom = _surface.View.WorldToScreen(new Point2D(0, rect.Bottom)).Y;
        var host = InkHost.ActualHeight;
        // 上方伸出视口底、且下方伸出视口顶，才算真的看不见。
        return top < host && bottom > 0;
    }

    /// <summary>让某一页在缓存里待命，并在落地时换上去。</summary>
    private void EnsureTier(int pageIndex, PdfResolutionPolicy.ResolutionTier tier)
    {
        // 同一个原生缺陷的同一个闸口（见 CanRasterize）。策略层照旧给出该要哪一档，
        // 但送进 PDFium 之前先过这一关 —— 崩在原生里，这里是唯一拦得住的地方。
        if (!CanRasterize(pageIndex)) return;

        if (_cache.TryGet(pageIndex, tier, out var cached) && cached is not null)
        {
            ApplyLayer(pageIndex, cached, tier);
            return;
        }

        // 移动中不发新请求 —— 这一条就是"滚动不卡"的全部机制。
        if (_policy.IsMovingFast) return;

        // 已经在路上、而且就是那一档：等它，别重复发。
        if (_pending.Any(p => p.PageIndex == pageIndex && p.Tier == tier)) return;

        var request = _cache.Request(pageIndex, tier);
        var entry = new PendingRequest { PageIndex = pageIndex, Tier = tier, Request = request };
        _pending.Add(entry);

        // 落地时换上去：即使这时用户已经滚走，也要换 ——
        // 因为这一档留在缓存里，下次滚回来直接就有；不换就是白花的那 25 ms。
        request.Completed += () =>
        {
            _pending.Remove(entry);
            var image = request.Result;
            if (image is null) return;
            ApplyLayer(pageIndex, image, tier);
        };
    }

    private void ApplyLayer(int pageIndex, BitmapImage image, PdfResolutionPolicy.ResolutionTier tier)
    {
        if (!_layers.TryGetValue(pageIndex, out var layer)) return;
        layer.Tier = tier;
        layer.Image.Source = image;
        layer.Image.Visibility = Visibility.Visible;
        UpdateLayerTransforms();
    }

    /// <summary>
    /// 预加载邻近页的<b>低档</b>，顺带把胶片条上那几格也填上。
    /// <para>
    /// <b>胶片缩略图与主视图的预加载是同一批请求</b>：两边要的都是 72 dpi 低档，
    /// 而缓存的键是 (页, 档) —— 于是胶片上那一格拿到的正是主视图滚动过去时会用到的那一块，
    /// <b>一份位图两处用</b>。分成两条路就意味着同一页被光栅化两遍（各 4 ms），
    /// 而 300 页的文档光这一项就多花一秒多。
    /// </para>
    /// <para>
    /// <b>目前只预加载当前这一页</b>，因为 PDFium 156.0.8066 有一个已实测的原生缺陷：
    /// <c>FPDF_RenderPageBitmap</c> 在 <c>page_index &gt;= 1</c> 时访问违例（0xC0000005，
    /// 托管层 catch 不到，整个进程走）。最小复现见 <c>tools/PdfProbe --minimal</c>，
    /// 3 页手写 PDF、绑定无误、page 0 成功 page 1 崩。
    /// 在换掉那份原生库之前，**碰第 2 页就是崩** —— 那宁可慢，也不能让用户开一份 PDF
    /// 就丢整个进程。理由与最小复现都记在 AGENTS 里。
    /// </para>
    /// </summary>
    /// <summary>
    /// 能不能安全地把这一页送去光栅化。
    /// <para>
    /// 目前<b>只有第 1 页</b>（页号 0）能渲染：PDFium 156.0.8066 的
    /// <c>FPDF_RenderPageBitmap</c> 在 <c>page_index &gt;= 1</c> 时访问违例，
    /// 0xC0000005、托管层接不到、整个进程走。最小复现是 3 页手写 PDF
    /// （<c>tools/PdfProbe &lt;pdf&gt; --minimal</c>）：绑定无误，page 0 成功、page 1 崩。
    /// </para>
    /// <para>
    /// 这一句是<b>整个窗口唯一的闸口</b>，而它必须存在：崩溃发生在原生里，
    /// <c>try/catch</c> 接不到，<c>Task</c> 的异常也接不到。
    /// 没有闸口的症状是"用户双击一份 PDF，应用整个消失"。
    /// </para>
    /// <para>
    /// <b>换掉那份原生库之后，把这里改成 <c>true</c> 即可</b>，其余代码不用动 ——
    /// 分辨率策略、预加载、胶片共用同一份位图那套都是照着"能渲染多页"写的。
    /// </para>
    /// </summary>
    private bool CanRasterize(int pageIndex) => pageIndex == 0;

    private void PreloadNeighbours()
    {
        if (_layout is null) return;

        // 胶片与主视图的预加载走的是同一批低档请求，键是 (页, 档)，
        // 所以同一个位图两处用（分成两条路就得把每一页光栅化两遍）。
        for (var index = Math.Max(0, _currentPage - FilmstripLead); index <= _layout.PageCount - 1; index++)
            EnsureFilmstripThumbnail(index);

        foreach (var index in new[] { _currentPage - 1, _currentPage + 1 })
        {
            if (index < 0 || index >= _layout.PageCount) continue;
            EnsureTier(index, PdfResolutionPolicy.ResolutionTier.Low);
        }
    }

    /// <summary>胶片上"视口之前预填几格"：够一屏半，于是往上滚时胶片总是已经画好的。</summary>
    private const int FilmstripLead = 6;

    /// <summary>要第 <paramref name="pageIndex"/> 格的低档缩略图（与主视图共用同一份位图）。</summary>
    private void EnsureFilmstripThumbnail(int pageIndex)
    {
        if (_filmstrip is null) return;
        if (_filmstrip.HasThumbnail(pageIndex)) return;

        if (!CanRasterize(pageIndex)) return;

        if (_cache.TryGet(pageIndex, PdfResolutionPolicy.ResolutionTier.Low, out var cached) && cached is not null)
        {
            _filmstrip.OnThumbnailReady(pageIndex, cached);
            return;
        }

        // 已经在路上就不重复发。
        if (_pending.Any(p => p.PageIndex == pageIndex && p.Tier == PdfResolutionPolicy.ResolutionTier.Low)) return;

        var request = _cache.Request(pageIndex, PdfResolutionPolicy.ResolutionTier.Low);
        var entry = new PendingRequest { PageIndex = pageIndex, Tier = PdfResolutionPolicy.ResolutionTier.Low, Request = request };
        _pending.Add(entry);
        request.Completed += () =>
        {
            _pending.Remove(entry);
            var image = request.Result;
            if (image is null) return;
            _filmstrip.OnThumbnailReady(pageIndex, image);
        };
    }

    // ────────────────────────── 变换与收尾 ──────────────────────────

    private void OnDocumentChanged(object? sender, InkDocumentChangedEventArgs e)
    {
        // 引擎没有 SelectionChanged 也不清选择集，所以订阅 Changed 一律作废
        // （理由同 PagedCanvasWindow 的第 6 条判断）。
        _filmstrip?.RefreshInk();
    }

    private void UpdateLayerTransforms()
    {
        if (_layout is null) return;
        var origin = _surface.View.WorldToScreen(new Point2D(0, 0));
        var axisX = _surface.View.WorldToScreen(new Point2D(1, 0));
        var axisY = _surface.View.WorldToScreen(new Point2D(0, 1));
        var scaleX = axisX.X - origin.X;
        var scaleY = axisY.Y - origin.Y;

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(scaleX, scaleY));
        group.Children.Add(new TranslateTransform(origin.X, origin.Y));

        foreach (var layer in _layerOrder)
        {
            var rect = _layout.PageRect(layer.Index);
            // 先把页矩形自己的世界位置走完（平移），再进世界→屏幕那一份。
            layer.Frame.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(scaleX, scaleY),
                    new TranslateTransform(origin.X + rect.Left * scaleX, origin.Y + rect.Top * scaleY),
                },
            };
        }
    }

    private void ResetView()
    {
        if (_layout is null || _layout.PageCount == 0) return;
        ZoomTo(1.0);
        // 开局对准第一页顶边，而不是文档原点 —— 后者会把上方 32 点的页边距顶到视口外，
        // 屏幕上第一眼是半页白边。
        ScrollToPage(0);
        UpdatePageLabel();
        RefreshVisibleTiers();
    }

    private void ShutdownPdf()
    {
        _settleTimer.Stop();
        _preloadTimer.Stop();
        foreach (var entry in _pending) entry.Request.Supersede();
        _pending.Clear();
        ClearPageLayers();
        _cache.Close();
        _cache.Dispose();
        _surface.Canvas.Document.Changed -= OnDocumentChanged;
        _surface.View.Viewport.Changed -= OnViewportChanged;
        _selection?.Detach(InkHost);
        _selection = null;
        _surface.DetachFrom(InkHost);
        _surface.Dispose();
    }

    private sealed class PendingRequest
    {
        internal required int PageIndex { get; init; }
        internal required PdfResolutionPolicy.ResolutionTier Tier { get; init; }
        internal required PdfPageCache.PdfPageRequest Request { get; init; }
    }

    private sealed class PageLayer
    {
        internal required int Index { get; init; }
        internal required Image Image { get; init; }
        internal required Border Frame { get; init; }
        internal PdfResolutionPolicy.ResolutionTier Tier { get; set; } = PdfResolutionPolicy.ResolutionTier.Low;
    }
}
