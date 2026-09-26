using System.Collections.Generic;
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
/// 一块<b>有页面内容</b>的全屏画布：底下有一页一页的东西，上面用现有的批注工具写。
/// <para>
/// 白板与图片批注都从这里派生：那边每页底下是一块纯色，这边每页底下是一张打开的图。
/// <b>页模型、缩略图导航、缩放漫游、以及整套"选择 / 框选 / 套索 / 缩放手柄 / 旋转柄"</b>
/// 全在这一份里 —— 它们与底下是纯色还是图毫无关系，而它们恰好是这一族里最贵的部分
/// （选择那套占白板近 700 行）。抄第二份的代价是以后改一次手势要改两处，
/// 而两处迟早会不一致，且不一致只在"某个场景手感不对"时显形，极难指认。
/// </para>
/// <para>
/// <b>派生类要做的只有五件事</b>：在 <see cref="InitializeCanvasHost"/> 里交出那几个命名元素、
/// 实现 <see cref="CreatePageCore"/> 与 <see cref="OnAddPageRequested"/>、
/// 以及实现 <see cref="ApplyPageBackdrops"/> 铺自己那一种底。
/// 其余全部共用。
/// </para>
/// <para>
/// <b>它不碰"底下是什么"以外的任何场景差异</b>：穿透、冻结、底色档位、停靠那些都留在派生类，
/// 因为它们是各自窗口的设置，不是"分页画布"这件事本身的性质。
/// </para>
/// </summary>
public abstract class PagedCanvasWindow : Window
{
    // ------------------------------------------------------------------ 手势容差

    /// <summary>点选的容差（<b>屏幕</b> DIP）：手指粗点一下也要挑得中，但大到会挑中隔壁那一笔。</summary>
    private const double PickToleranceScreen = 12;

    /// <summary>按下与抬起差多少算"点了一下"而不是"拖了一个框"（屏幕 DIP）。</summary>
    private const double ClickSlopScreen = 4;

    /// <summary>手柄命中比"画出来的那一颗"宽出来的容差（屏幕 DIP）：手指点手柄不能要求点得准。</summary>
    private const double HandleSlopScreen = 4;

    /// <summary>捏合的上下限：拉到 0.2 倍看得见整页，拉到 8 倍够写最细的字。</summary>
    protected const double MinZoom = 0.2;

    protected const double MaxZoom = 8;

    protected enum Drag
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

    // ------------------------------------------------------------------ 状态

    private readonly List<CanvasPage> _pages = [];
    private readonly List<Button> _thumbnailCards = [];
    private readonly SelectionAdorner _adorner = new();
    private readonly TouchGestureTracker _gestures = new();
    private readonly List<Point2D> _lassoWorld = [];

    private CanvasSurface _surface = null!;
    private Popup _thumbnailPopup = null!;
    private ScrollViewer _thumbnailScroll = null!;
    private StackPanel _thumbnailList = null!;
    private int _activePageIndex;

    private Grid _inkHost = null!;
    private Grid _pageControlHost = null!;
    private Button _previousPageButton = null!;
    private Button _nextPageButton = null!;
    private Button _addPageButton = null!;
    private TextBlock _pageNumberText = null!;

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
    /// 正在做一次<b>会重写点数据</b>的变换（拖动里的挪 / 缩 / 转，或派生类发起的一次整页变换）。
    /// 文档变更处理器靠它分清"自己改的"与"别人改的"：前者只重推几何，后者要让选择作废。
    /// </summary>
    private bool IsTransforming => _drag is Drag.Move or Drag.Scale or Drag.Rotate || _externalTransform;

    /// <summary>派生类正在做一次"不是拖动、但同样会重写点数据"的变换（整页旋转走这一条）。</summary>
    private bool _externalTransform;

    /// <summary>
    /// 做一次<b>整篇的变换</b>（例如"图与笔迹一起转 90°"里的那半句）。
    /// <para>
    /// 存在的理由：<see cref="OnDocumentChanged"/> 在"不是拖动"时会<b>清掉选择</b>，
    /// 而"全选 → 旋转"这件事的第一步恰好就是让选择变成全篇 —— 于是它会被自己清掉，
    /// 旋转落在一篇空的选区上，<b>而且不报任何错</b>，症状是"图转了，笔迹没转"。
    /// </para>
    /// <para>
    /// 所以这里给出的是一个与拖动同性质的窗口：变换期间来的文档变更按"自己改的"处理，
    /// 只重推几何不清选择。批的收口仍由调用方决定（本方法不擅自开批）——
    /// "一次旋转 = 一步撤销"那条要由调用方连同图那一侧的变更一起算。
    /// </para>
    /// </summary>
    protected void RunDocumentTransform(Action<Dusk.Ink.Document.InkDocument> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (_externalTransform) return;
        _externalTransform = true;
        try { transform(Surface.Document); }
        finally { _externalTransform = false; }
    }

    private bool _hostReady;

    // ------------------------------------------------------------------ 派生类要实现的

    /// <summary>层级登记用的名字（"白板" / "图片批注"）。<b>它在日志与自检里指认是哪一块</b>。</summary>
    protected abstract string CanvasLayerName { get; }

    /// <summary>造一页。<b>只造，不激活</b>；激活走 <see cref="ActivatePage"/>。</summary>
    protected abstract CanvasPage CreatePageCore();

    /// <summary>
    /// 点"新建"那一下要做什么。<b>不是"造一页空白"</b>：白板是加一张空页，
    /// 图片那边是拉起文件选择框（一个文件一页），两者形状不同，所以这一条留给派生类。
    /// </summary>
    protected abstract void OnAddPageRequested();

    /// <summary>
    /// 把底下那块东西铺到位：当前这一页的宿主底 + <b>每一页</b>的缩略图底。
    /// <para>
    /// 两条都要在这里做，因为换页时宿主底与缩略图底会同时失效（缩略图那几页没被激活过）。
    /// </para>
    /// </summary>
    protected abstract void ApplyPageBackdrops();

    // ------------------------------------------------------------------ 宿主接线

    /// <summary>
    /// 把标记里那几个命名元素交给基类，<b>派生类构造时在 <c>InitializeComponent()</c> 之后立刻调</b>。
    /// <para>
    /// 为什么不能由基类自己去 <c>FindName</c>：基类构造函数先于 <c>InitializeComponent()</c> 跑，
    /// 那时元素还不存在，基类拿到的会是 null 而"不报错"，等到第一帧才炸。
    /// 让派生类显式交出来，编译期就少一个能悄悄传错的东西。
    /// </para>
    /// </summary>
    protected void InitializeCanvasHost(
        Grid inkHost,
        Grid pageControlHost,
        Button previousPageButton,
        Button nextPageButton,
        Button addPageButton,
        TextBlock pageNumberText)
    {
        ArgumentNullException.ThrowIfNull(inkHost);
        ArgumentNullException.ThrowIfNull(pageControlHost);
        ArgumentNullException.ThrowIfNull(previousPageButton);
        ArgumentNullException.ThrowIfNull(nextPageButton);
        ArgumentNullException.ThrowIfNull(addPageButton);
        ArgumentNullException.ThrowIfNull(pageNumberText);

        _inkHost = inkHost;
        _pageControlHost = pageControlHost;
        _previousPageButton = previousPageButton;
        _nextPageButton = nextPageButton;
        _addPageButton = addPageButton;
        _pageNumberText = pageNumberText;
        _hostReady = true;
    }

    /// <summary>
    /// 派生类在 <c>InitializeCanvasHost</c> 之后调这一条，完成共用部分的装配。
    /// <para>
    /// 拆成两步而不是让派生类构造函数调一个"什么参数都不给"的 <c>InitializeShared()</c>：
    /// 那样子类就必须记住调用顺序，而顺序错了（例如漏了 <c>InitializeComponent</c>）
    /// 表现是"构造到一半空引用"，报错点离原因很远。
    /// </para>
    /// </summary>
    protected void InitializeSharedCanvas()
    {
        if (!_hostReady)
            throw new InvalidOperationException("InitializeCanvasHost 必须在 InitializeSharedCanvas 之前调用。");

        CreateThumbnailPopup();
        BindPageIcon(_previousPageButton);
        BindPageIcon(_nextPageButton);
        BindPageIcon(_addPageButton);

        var firstPage = CreatePageCore();
        _pages.Add(firstPage);
        _surface = firstPage.Surface;
        _surface.AttachTo(_inkHost, 0);
        SubscribeSurface(_surface);

        // 选择层在墨迹<b>之上</b>：加在面之后。它不吃命中（IsHitTestVisible=false），
        // 所有输入都由本窗口自己按坐标判。
        _inkHost.Children.Add(_adorner);

        _pageNumberText.MouseLeftButtonUp += (_, _) => ToggleThumbnailMenu();
        _previousPageButton.Click += (_, _) => ActivateRelativePage(-1);
        _nextPageButton.Click += (_, _) => ActivateRelativePage(1);
        _addPageButton.Click += (_, _) => OnAddPageRequested();

        ApplyPageBackdrops();
        UpdatePageControl();

        // 输入：<b>handledEventsToo = true</b>。引擎在选择态虽然什么都不写，
        // 但它仍会把指针事件标成已处理（OnPointerDownHandler 末尾那一句），
        // 不带着一句就永远收不到落点 —— 而"收不到"没有任何症状，只是选择不动。
        _inkHost.AddHandler(PointerDownEvent, new PointerDownEventHandler(OnPointerDown), true);
        _inkHost.AddHandler(PointerMoveEvent, new PointerMoveEventHandler(OnPointerMove), true);
        _inkHost.AddHandler(PointerUpEvent, new PointerUpEventHandler(OnPointerUp), true);
        _inkHost.AddHandler(PointerCancelEvent, new PointerCancelEventHandler(OnPointerCancel), true);

        // Delete 摘掉选中的那些笔迹（整批一步撤销）。只在"有页面"的画布里才有意义。
        PreviewKeyDown += (_, e) =>
        {
            if (_pageControlHost.IsKeyboardFocusWithin || _thumbnailPopup.IsOpen) return;
            if (e.Key != Key.Delete || !_surface.IsSelectMode) return;
            e.Handled = DeleteSelection();
        };

        OnSharedCanvasReady();
    }

    /// <summary>
    /// 共用部分装好之后叫一次，<b>此时墨迹面已经在宿主里、且在索引 0</b>。
    /// <para>
    /// 派生类要往墨迹<b>底下</b>塞东西（图片批注那张图）就得靠这一条：自己插到 0 会把墨迹面挤到 1，
    /// 于是图压在墨迹上面 —— 而"垫底"这件事反了不会报错，只表现为"图上的笔迹被图盖住"。
    /// </para>
    /// </summary>
    protected virtual void OnSharedCanvasReady()
    {
    }

    /// <summary>
    /// 视口动过之后叫一次（除了基类自己那份"橡皮半径重发 + 选框重画"之外）。
    /// <para>派生类要跟着视口动的东西挂这里：图片批注那张图就是靠它把 world→screen 矩阵重算一遍。</para>
    /// </summary>
    protected virtual void OnViewportChangedCore()
    {
    }

    private static void BindPageIcon(Button button)
    {
        if (button.Content is IconElement icon) IconInk.Apply(button, icon);
    }

    // ------------------------------------------------------------------ 对外

    /// <summary>
    /// 当前这块画布的墨迹面。工具栏要写的一切（模式、颜色、粗细、擦法、撤销）都从这里走。</summary>
    internal CanvasSurface Surface => _surface;

    /// <summary>
    /// 垫在墨迹面底下那个格子。<b>派生类往里插东西必须插在索引 0</b>（见 <see cref="OnSharedCanvasReady"/>）。
    /// <para>
    /// 名字叫 <c>InkHostGrid</c> 而不是 <c>InkHost</c>：派生窗口的标记里那个 <c>x:Name="InkHost"</c>
    /// 会被源码生成器生成一个同名<b>字段</b>，与基类这个属性撞出 CS0108。
    /// 那个字段是各派生窗口私有的（白板用它设底色、图片那块用它插图），基类要的是"同一块"的公共看法，
    /// 所以这两者刻意用两个名字，而不是让一个藏掉另一个。
    /// </para>
    /// </summary>
    protected Grid InkHostGrid => _inkHost;

    /// <summary>
    /// 全部页。<b>给派生类铺底与读页用</b>：白板要拿它给每页的缩略图刷同一档底色，
    /// 图片那边要拿它给每页刷自己那张图。
    /// <para>只读是有意的 —— 页的增删必须走 <see cref="AddPage"/> 与基类里的换页那条路，
    /// 直接往这个列表里塞会让索引、缩略图、当前面三者对不上，而那种错只在换页时显形。</para>
    /// </summary>
    protected IReadOnlyList<CanvasPage> Pages => _pages;

    internal bool IsSelecting => _surface.IsSelectMode;

    internal Rect? AdornerMarquee => _adorner.Marquee;

    internal IReadOnlyList<Point>? AdornerFrameCorners => _adorner.FrameCorners;

    internal IReadOnlyList<Point>? AdornerHandles => _adorner.Handles;

    internal int PageCount => _pages.Count;

    internal int ActivePageIndex => _activePageIndex;

    internal bool ThumbnailPopupOpen => _thumbnailPopup.IsOpen;

    internal int ThumbnailCardCount => _thumbnailCards.Count;

    internal IReadOnlyList<Button> ThumbnailCards => _thumbnailCards;

    internal ScrollViewer ThumbnailScroll => _thumbnailScroll;

    internal UIElement ThumbnailPlacementTarget => _thumbnailPopup.PlacementTarget!;

    internal PlacementMode ThumbnailPlacement => _thumbnailPopup.Placement;

    internal event Action? ActivePageChanged;

    internal event Action? HistoryStateChanged;

    /// <summary>
    /// 派生类的底要重铺时（换设置、换图、转朝向）调这一条。
    /// <para>不直接暴露 <see cref="ApplyPageBackdrops"/>：那是"怎么铺"，这里多一层"什么时候铺"，
    /// 于是派生类不会在只改了宿主底时忘了顺带刷缩略图那几页。</para>
    /// </summary>
    protected void RefreshPageBackdrops() => ApplyPageBackdrops();

    // ------------------------------------------------------------------ 缩略图

    private void CreateThumbnailPopup()
    {
        _thumbnailList = new StackPanel();
        _thumbnailScroll = new ScrollViewer
        {
            Content = _thumbnailList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            PanningMode = PanningMode.VerticalOnly,
            CanContentScroll = false,
        };

        var surface = new Border
        {
            Width = 208,
            MaxHeight = 520,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = default,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "FlyoutSurfaceBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        surface.Child = _thumbnailScroll;

        _thumbnailPopup = new Popup
        {
            PlacementTarget = _pageControlHost,
            Placement = PlacementMode.Top,
            HorizontalOffset = 0,
            VerticalOffset = -8,
            IsLightDismissEnabled = true,
            StaysOpen = false,
            ShouldConstrainToRootBounds = true,
            Child = surface,
        };
        ((Grid)Content!).Children.Add(_thumbnailPopup);
    }

    private void ToggleThumbnailMenu()
    {
        if (_thumbnailPopup.IsOpen)
        {
            _thumbnailPopup.IsOpen = false;
            return;
        }

        RebuildThumbnailCards();
        _thumbnailPopup.IsOpen = true;
        ScrollToActiveThumbnail();
    }

    private void RebuildThumbnailCards()
    {
        _thumbnailList.Children.Clear();
        _thumbnailCards.Clear();
        var pageCount = _pages.Count;

        // 底与内容尺寸由派生类刚刚铺过（ApplyPageBackdrops 一次刷全部），
        // 这里只补尺寸与刷新，不再各自去读设置 —— 两处各读一次就会在某次改动后只对一半。
        for (var index = 0; index < pageCount; index++)
        {
            var pageIndex = index;
            var page = _pages[index];
            page.Thumbnail.Width = 180;
            page.Thumbnail.Height = 96;
            page.Thumbnail.Refresh();

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            content.Children.Add(page.Thumbnail);
            var label = new TextBlock
            {
                Text = $"第 {index + 1} 页",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            Grid.SetRow(label, 1);
            content.Children.Add(label);

            var card = new Button
            {
                Content = content,
                Width = 192,
                Height = 128,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            card.SetResourceReference(Control.BackgroundProperty, "SubtleFillColorTransparentBrush");
            card.Click += (_, _) =>
            {
                ActivatePage(pageIndex);
                _thumbnailPopup.IsOpen = false;
            };
            AutomationProperties.SetName(card, $"第 {pageIndex + 1} 页，共 {pageCount} 页");
            _thumbnailCards.Add(card);
            _thumbnailList.Children.Add(card);
        }

        UpdateThumbnailSelection();
    }

    private void UpdateThumbnailSelection()
    {
        for (var index = 0; index < _thumbnailCards.Count; index++)
            _thumbnailCards[index].SetResourceReference(
                Control.BackgroundProperty,
                index == _activePageIndex ? "AccentFillColorDefaultBrush" : "SubtleFillColorTransparentBrush");
    }

    private void ScrollToActiveThumbnail()
    {
        if (_activePageIndex < 0 || _activePageIndex >= _thumbnailCards.Count) return;
        var card = _thumbnailCards[_activePageIndex];
        Dispatcher.BeginInvoke(() =>
        {
            if (_thumbnailPopup.IsOpen) _thumbnailScroll.ScrollToElement(card);
        });
    }

    // ------------------------------------------------------------------ 分页

    /// <summary>
    /// 造一页并激活它。<b>共用的一条路</b>：白板调它加一张空页，图片那边调它加一个文件。
    /// </summary>
    protected void AddPage(CanvasPage page)
    {
        if (_drag != Drag.None || _gestures.IsActive || _surface.History.HasOpenBatch) return;

        _pages.Add(page);
        ActivatePage(_pages.Count - 1);
        if (_thumbnailPopup.IsOpen)
        {
            RebuildThumbnailCards();
            ScrollToActiveThumbnail();
        }
    }

    private void ActivateRelativePage(int offset) => ActivatePage(_activePageIndex + offset);

    private void ActivatePage(int index)
    {
        if (index < 0 || index >= _pages.Count || index == _activePageIndex) return;
        if (_drag != Drag.None || _gestures.IsActive || _surface.History.HasOpenBatch) return;

        ClearSelection();
        ResetTransientState();

        var previous = _surface;
        previous.DetachFrom(_inkHost);
        UnsubscribeSurface(previous);

        _activePageIndex = index;
        _surface = _pages[index].Surface;
        _surface.AttachTo(_inkHost, 0);
        SubscribeSurface(_surface);

        ApplyPageBackdrops();
        UpdatePageControl();
        UpdateThumbnailSelection();
        ScrollToActiveThumbnail();
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
        _pageNumberText.Text = $"{pageNumber} / {pageCount}";
        _previousPageButton.IsEnabled = _activePageIndex > 0;
        _nextPageButton.IsEnabled = _activePageIndex < pageCount - 1;
        AutomationProperties.SetName(_previousPageButton, "上一页");
        AutomationProperties.SetName(_pageNumberText, $"第 {pageNumber} 页，共 {pageCount} 页");
        AutomationProperties.SetName(_nextPageButton, "下一页");
        AutomationProperties.SetName(_addPageButton, "新建页面");
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

    private void OnSurfaceHistoryStateChanged() => HistoryStateChanged?.Invoke();

    /// <summary>
    /// 视口动了：<b>橡皮半径要重发一次</b>（它是世界单位，而用户手上的刻度是屏幕像素），
    /// 选框则只要重画 —— 它存的是世界系，换算在画的那一步做。
    /// </summary>
    private void OnViewportChanged(object? sender, EventArgs e)
    {
        _surface.ReapplyEraserRadius();
        _adorner.InvalidateVisual();
        OnViewportChangedCore();
    }

    /// <summary>
    /// 文档变了。<b>引擎没有 SelectionChanged，也不反向清理选择集</b>（被擦掉的编号会一直留在里面），
    /// 所以"选择还新不新"这件事只有宿主自己管得着：不在拖动的当下，一律取消选择。
    /// <para>不这么做会留下"框里是空的、却还能一拖拖走一串不存在的笔迹"，而撤销会撤到别的东西上。</para>
    /// </summary>
    private void OnDocumentChanged(object? sender, InkDocumentChangedEventArgs e)
    {
        if (_thumbnailPopup.IsOpen && _activePageIndex >= 0 && _activePageIndex < _pages.Count)
            _pages[_activePageIndex].Thumbnail.Refresh();

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
        //    挑的常常是一小片密字，逐笔点中再拖不现实。
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
    /// 不这么做的话"想捏合却先拖了一下"会把内容留在挪歪的位置上，而用户从没打算挪它。
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

    // ------------------------------------------------------------------ 收尾

    /// <summary>
    /// 拆掉所有墨迹面。<b>必须显式</b>：Jalium 不代调，而每块面都挂着整棵墨迹视觉树（见 AGENTS）。
    /// </summary>
    protected virtual void DisposeSharedCanvas()
    {
        UnsubscribeSurface(_surface);
        DisposeDerivedResources();

        _thumbnailPopup.IsOpen = false;
        _thumbnailList.Children.Clear();
        _thumbnailCards.Clear();
        foreach (var page in _pages)
        {
            page.Surface.Dispose();
        }
        _pages.Clear();
    }

    /// <summary>派生类在收尾时拆自己那一份资源（图片页要放掉位图）。<b>基类不替它做</b>。</summary>
    protected virtual void DisposeDerivedResources()
    {
    }
}
