using Microsoft.Win32;
using FluentJalium.Controls;
using FluentJalium.Themes;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 浮动批注栏。<b>它的按钮是数据驱动的</b> —— 标记里只有一个空的 <c>ToolsPanel</c>，
/// 钮由 <see cref="ToolbarTools"/> 那份列表建出来。
/// <para>
/// <b>为什么值得这么改</b>：在这之前，六颗钮写死在标记里，而"当前这支笔长什么样"只有一份全局值。
/// 于是"我想再要一支红笔"这件事既没地方声明，也没地方存。
/// 现在按钮是一项数据，数据里带着它自己的颜色 / 粗细 / 笔型 / 笔锋 ——
/// 两个笔按钮各自一套，天然互不影响。
/// </para>
/// <para>
/// <b>本类只做三件事</b>：把列表渲染成控件、把"点哪一颗"翻译成"选中哪一项"、
/// 把选中项和二级菜单的编辑**转发**给数据模型。
/// 它<b>不持有任何工具数据</b>：以前那五个 <c>_currentPenXxx</c> / <c>_currentEraserXxx</c> 字段
/// 已经删掉，一处缓存就是第二个真相。
/// </para>
/// </summary>
public partial class AnnotationToolbarWindow : Window
{
    private bool _toolSync;
    private bool _isClosing;
    private AnnotationOverlayWindow? _annotationOverlay;
    private WhiteboardWindow? _whiteboard;
    private ImageViewerWindow? _imageViewer;
    private PdfViewerWindow? _pdfViewer;
    private bool _pdfViewerPresented;
    private SettingsWindow? _settingsWindow;
    private PenSecondaryMenuWindow? _penMenuWindow;
    private bool _penMenuVisible;
    private EraserSecondaryMenuWindow? _eraserMenuWindow;
    private bool _eraserMenuVisible;
    private TouchDevice? _touchDragDevice;

    /// <summary>
    /// 批注栏的<b>内容</b>（标记里那个根 <see cref="Grid"/>）。重父化搬的就是它。
    /// <para>存成字段而不是每次去读 <see cref="Window.Content"/>：搬进图片窗口那一刻，
    /// <c>Content</c> 的值没变但它的<b>视觉父亲</b>变了；字段是稳定的身份，落回去时对着同一个对象搬回去。</para>
    /// </summary>
    private readonly Grid _contentRoot;

    /// <summary>
    /// 批注栏这一份视觉<b>当前挂在哪个窗口下</b>。
    /// <para>
    /// 正常是自己那个窗口；窗口模式下它是图片窗口里的一个控件，那时就是图片窗口。
    /// 二级菜单、拖动、跟随宿主移动这几件事全都读它 —— 读 <c>this</c> 的话，
    /// 重父化期间算出来的坐标会以工具栏窗口的 <c>Left/Top</c> 为基准，而那对值此时是<b>陈旧的</b>
    /// （窗口已经 <c>Hide</c>，没人更新它），症状是浮窗出现在离按钮很远的地方。
    /// </para>
    /// </summary>
    private Window? _hostWindow;

    /// <summary>批注栏视觉现在挂在哪一格里。存下来是因为"摘回去"要知道从哪一格摘，
    /// 而 <see cref="_hostWindow"/> 是 <see cref="Window"/>，它自己<b>没有 <c>Children</c></b>
    /// （<c>Children</c> 是 Panel 的成员）—— 只存窗口的话摘不回来。</summary>
    private Grid? _hostGrid;

    /// <summary>视觉是否已经搬进别的窗口（也就是「工具栏是图片窗口的控件」这个状态）。</summary>
    internal bool IsRehosted { get; private set; }

    /// <summary>批注栏那个窗口现在<b>自己</b>持有内容吗（= 它是独立窗口，而不是空壳）。</summary>
    internal bool OwnContentIsBack => ReferenceEquals(Content, _contentRoot);

    /// <summary>
    /// 批注栏自己那个窗口在屏吗 —— <b>只听 <c>Shown</c> / <c>Hiding</c></b>。
    /// <para>
    /// 本来想用 <c>Window.IsVisible</c>，实测它在"搬进图片窗口又搬回来"这一趟里两次都不可靠：
    /// 窗口已经 <c>Hide()</c> 了它仍报 <c>true</c>，而 <c>Window.Visibility</c> 在 WPF 那一族里
    /// 本来就是"从没 Show 过"的窗口上初值不可靠（项目规矩第 4 条也是这个理由，层级系统早就为此
    /// 改听事件）。既然已经有一对事件，就以它为准 —— 层级那边消费的也是同一份真相。
    /// </para>
    /// </summary>
    internal bool IsShown { get; private set; }

    /// <summary>
    /// 把批注栏<b>整棵视觉</b>搬进 <paramref name="host"/>，自己那个窗口收起来。
    /// <para>
    /// 这就是「窗口模式下批注栏是图片窗口里的一个控件」的全部实现 —— 搬视觉，不是搬一个矩形。
    /// 两个各画各的窗口永远对不齐：图片窗口改尺寸时它不知道、它那一根窗框还压着图片、
    /// 拖手柄一动两者用两套坐标算位移，于是错位永远修不掉。
    /// </para>
    /// <para><b>反过来说，全屏模式下不能走这里</b>：那时批注栏要盖在图片上面浮着，
    /// 必须是独立窗口，否则它会占掉布局高度、把画面挤掉一块。</para>
    /// </summary>
    internal void RehostInto(Grid host, Window hostWindow)
    {
        if (IsRehosted || ReferenceEquals(hostWindow, this)) return;

        // 摘下来只有一条路：把 Content 置空。
        // 框架没给 Panel 暴露 DetachFromVisualParent（那是 Popup 的内部办法），
        // 而 Window.Content 是 DP —— 置空就是"视觉父亲变成 null"这一步本身。
        Content = null;
        host.Children.Add(_contentRoot);
        _hostWindow = hostWindow;
        _hostGrid = host;
        IsRehosted = true;

        // 自己那个窗口收起来：内容已经在别人那儿了，再 Show 一次就是同一份视觉画两遍。
        Hide();

        hostWindow.LocationChanged += OnHostWindowLocationChanged;
        hostWindow.SizeChanged += OnHostWindowSizeChanged;
        PositionSecondaryMenus();
    }

    /// <summary>
    /// 把批注栏的视觉搬回自己那个窗口并显示。
    /// <para>
    /// 退路必须<b>只有一个</b>：批注栏是 <c>app.MainWindow</c>，它收起来而没有搬回去的话，
    /// 整条工具栏会跟着图片窗口一起消失，而图片窗口自己又没有「关掉自己」以外的出路。
    /// 所以图片窗口的每一处收尾（切回屏幕批注、关窗、Dispose）都调它，且允许重复调用。
    /// </para>
    /// </summary>
    internal void RestoreFromHost()
    {
        if (!IsRehosted) return;

        if (_hostGrid is not { } host || _hostWindow is not { } hostWindow) return;

        hostWindow.LocationChanged -= OnHostWindowLocationChanged;
        hostWindow.SizeChanged -= OnHostWindowSizeChanged;

        host.Children.Remove(_contentRoot);
        // 挂回窗口不能直接重设成同一个值：DP 那一趟会被当成「没变」而什么都不做。
        // 先摘成 null 再挂回去，才真的把视觉父亲换回窗口自己。
        Content = _contentRoot;
        _hostWindow = null;
        _hostGrid = null;
        IsRehosted = false;

        // 可见性只听 Shown/Hiding（层级那套的规矩），所以再 Show 一次就会重新登记。
        Show();
        PositionSecondaryMenus();
    }

    private void OnHostWindowLocationChanged(object? sender, EventArgs e) => PositionSecondaryMenus();

    private void OnHostWindowSizeChanged(object sender, SizeChangedEventArgs e) => PositionSecondaryMenus();

    /// <summary>这一趟触摸拖动<b>动的是哪个窗口</b>：重父化期间是宿主，否则是工具栏自己。</summary>
    private Window? _touchDragOwner;

    private Point _touchDragStartScreenPoint;
    private double _touchDragStartWindowLeft;
    private double _touchDragStartWindowTop;

    /// <summary>
    /// 已渲染的按钮：项标识 → 控件。给二级菜单定位、键盘移动与探针用。
    /// <para>
    /// 类型是 <see cref="FrameworkElement"/> 而不是 <c>Control</c>：分隔线是一条 <see cref="Border"/>，
    /// 而 Border 属于 Decorator 那一支、不是 Control —— 用 Control 会把分隔线挡在类型外，
    /// 逼得给分隔线单独开一条存储路径。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, FrameworkElement> _toolControls = new(StringComparer.Ordinal);

    /// <summary>按显示顺序的按钮控件。键盘左右移动读它（不是读字典）。</summary>
    private readonly List<FrameworkElement> _toolOrder = [];

    /// <summary>
    /// 上一次渲染的"结构签名"（标识 + 类型，按顺序）。
    /// <para>
    /// 数据变了有两种：改颜色（结构没变）与增删换序（结构变了）。
    /// 前者只需刷新那一颗的外观，后者才要重建控件 —— 而重建控件会丢掉键盘焦点，
    /// 所以拖滑杆这类高频改动绝不能走重建。
    /// </para>
    /// </summary>
    private string _toolSignature = string.Empty;

    /// <summary>
    /// 画布此刻是不是在屏上。<b>自己记而不是读 <c>Visibility</c></b>：
    /// 这里判错的后果是"该截的时候没截"或者"多截一张"，而多截的那一张里已经带着上一轮的笔记
    /// （冻结底图是画在笔记下面的，烙进去就擦不掉了）。
    /// </summary>
    private bool _canvasPresented;

    /// <summary>
    /// 自上一次截屏之后，用户是否已经离开过书写（去做别的事了）。
    /// <para>
    /// 它要单独记一笔，是因为穿透模式下画布<b>一直留在屏上</b> ——
    /// 只看 <see cref="_canvasPresented"/> 的话，"用户在鼠标模式下操作了半天再回来写"
    /// 与"在画布里换了一下橡皮"长得一模一样，而这两件事该不该重截屏正好相反。
    /// </para>
    /// </summary>
    private bool _leftDrawingSession;

    private Border DragHandle => (Border)DragHandleChrome!;

    /// <summary>画布此刻在不在屏上。验收读它（"穿透模式下画布仍然显示"那条）。</summary>
    internal bool CanvasPresented => _canvasPresented;

    /// <summary>本窗口持有的那块画布（可能是 <c>null</c>：它是懒创建的）。
    /// 给验收读穿透位与冻结底图 —— 这两件事都只有画布自己知道。</summary>
    internal AnnotationOverlayWindow? Canvas => _annotationOverlay;

    /// <summary>白板此刻在不在屏上。与 <see cref="_canvasPresented"/> 各记各的：
    /// 两块画布不会同时在屏，但"谁在屏上"这件事不能共用一个布尔 ——
    /// 切场景时一边隐藏会把另一边的状态一起抹掉。</summary>
    private bool _whiteboardPresented;

    /// <summary>验收读它：白板在不在屏上。</summary>
    internal bool WhiteboardPresented => _whiteboardPresented;

    /// <summary>本窗口持有的那块白板（可能是 <c>null</c>：它也是懒创建的）。</summary>
    internal WhiteboardWindow? Whiteboard => _whiteboard;

    /// <summary>图片批注这块画布此刻在不在屏。与白板各记各的，理由同上。</summary>
    private bool _imageViewerPresented;

    /// <summary>验收读它：图片批注在不屏上。</summary>
    internal bool ImageViewerPresented => _imageViewerPresented;

    /// <summary>本窗口持有的图片批注窗口（可能是 <c>null</c>：懒创建）。</summary>
    internal ImageViewerWindow? ImageViewer => _imageViewer;

    /// <summary>
    /// <b>此刻该被写的那块面</b>：白板在眼前就是白板那块，否则是批注那块；没建起来时是 <c>null</c>。
    /// <para>
    /// 工具栏上那些"写到画布去"的动作（撤销、重做、清空、把选中项的数据下发）一律经它，
    /// 不再各自写 <c>_annotationOverlay?.</c> —— 那样做的话，白板在眼前时按撤销
    /// 会安静地撤掉<b>另一块画布</b>的历史，而这既不报错也看不出来。
    /// </para>
    /// </summary>
    private CanvasSurface? ActiveSurface => CanvasSceneState.Active switch
    {
        CanvasScene.Whiteboard => _whiteboard?.Surface,
        CanvasScene.ImageCanvas => _imageViewer?.Surface,
        // PDF 那块面**住在 PDF 窗口里**，但"所有写到画布去的动作"仍要经 ActiveSurface ——
        // 不这么做的症状是：在 PDF 上按撤销，安静地撤掉了另一块画布的历史。
        CanvasScene.PdfCanvas => _pdfViewer?.Surface,
        _ => _annotationOverlay?.Surface,
    };

    /// <summary>工具栏上某一项的按钮控件；没有这一项时返回 <c>null</c>。</summary>
    internal FrameworkElement? FindToolControl(string id) => _toolControls.GetValueOrDefault(id);

    /// <summary>按显示顺序的全部按钮控件。</summary>
    internal IReadOnlyList<FrameworkElement> ToolControls => _toolOrder;

    public AnnotationToolbarWindow()
    {
        AllowsTransparency = true;
        InitializeComponent();
        _contentRoot = (Grid)Content!;
        SystemBackdrop = WindowBackdropType.None;
        Background = null;
        Opacity = 1;

        BuildToolControls();
        WireDragHandle();

        // 层级登记：批注栏层。之后本类里<b>不再出现任何 Topmost 赋值</b> ——
        // "工具栏在画布之上、菜单在工具栏之上、设置在全部之上"由 WindowLayerManager 排；
        // 本类只说一件与用户偏好有关的事：鼠标模式下要不要也压过其他应用。
        WindowLayerManager.Register(this, WindowLayer.Toolbar, "批注栏");
        WindowLayerManager.SetPinned(this, AppPreferences.Current.KeepToolbarOnTop);

        ToolbarTools.LayoutChanged += SyncToolControls;
        ToolbarTools.SelectionChanged += OnToolSelectionChanged;
        CanvasOptions.Changed += OnCanvasOptionsChanged;
        CanvasSceneState.Changed += OnCanvasSceneChanged;
        AppPreferences.Changed += OnPreferencesChanged;
        LocationChanged += (_, _) =>
        {
            // 重父化期间这一条不再描述批注栏的位置（视觉在图片窗口里，宿主挪了这里也不动），
            // 挪图片窗口是 OnHostWindowLocationChanged 管的。
            if (IsRehosted) return;
            if (_penMenuVisible) PositionPenSecondaryMenu();
            if (_eraserMenuVisible) PositionEraserSecondaryMenu();
        };
        Hiding += (_, _) =>
        {
            IsShown = false;
            EndTouchDrag(DragHandle);
        };
        Shown += (_, _) => IsShown = true;
        SystemSettingsChanged += (_, _) => FluentThemeManager.RefreshSystemTheme();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_penMenuVisible) HidePenSecondaryMenu();
            else if (_eraserMenuVisible) HideEraserSecondaryMenu();
            else if (_whiteboard is not null && _whiteboard.ClearSelectionForEscape())
            {
                // 白板里选着东西时，Esc 的第一件事是"取消选择"而不是"退出这块画布"：
                // 一次 Esc 就把整块白板收掉，用户下一次进来会以为是笔迹没了。
            }
            else if (_imageViewer is not null && _imageViewer.ClearSelectionForEscape())
            {
                // 图片批注同一条：选着东西时 Esc 先清选择。
            }
            else ToolbarTools.Select(FirstToolId(ToolbarToolKind.Mouse));
            e.Handled = true;
        };
        Closed += (_, _) =>
        {
            _isClosing = true;
            EndTouchDrag(DragHandle);
            ToolbarTools.LayoutChanged -= SyncToolControls;
            ToolbarTools.SelectionChanged -= OnToolSelectionChanged;
            CanvasOptions.Changed -= OnCanvasOptionsChanged;
            CanvasSceneState.Changed -= OnCanvasSceneChanged;
            AppPreferences.Changed -= OnPreferencesChanged;
            _penMenuWindow?.Close();
            _penMenuWindow = null;
            _penMenuVisible = false;
            _eraserMenuWindow?.Close();
            _eraserMenuWindow = null;
            _eraserMenuVisible = false;
            _settingsWindow?.Close();
            _settingsWindow = null;
            DisposeAnnotationOverlay();
            DisposeWhiteboard();
            DisposeImageViewer();
        };

        SyncToolControls();
        SyncCanvasOverlay();
    }

    /// <summary>
    /// Jalium 26.10.x 的 Window.SizeToContent 尚不能可靠地按透明浮窗内容收缩，
    /// 因此与笔二级菜单一致，显式测量内容。这样工具组和独立拖动柄不会被固定 300px 宿主裁切。
    /// </summary>
    private void FitSizeToContent()
    {
        if (Content is not FrameworkElement root)
            return;

        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SizeToContent = SizeToContent.Manual;
        var desired = root.DesiredSize;
        var previousWidth = Width;
        Width = Math.Max(1, Math.Ceiling(desired.Width));
        Height = Math.Max(1, Math.Ceiling(desired.Height));

        // 宽度变了守住"中心不动"而不是把右缘推出去：栏是用户摆的（启动时是屏幕正下方居中），
        // 而加一颗钮就多 44 DIP —— 若 Left 不变，"居中"在第一次自定义之后就失真了。
        if (previousWidth > 0 && !double.IsNaN(previousWidth)) Left -= (Width - previousWidth) / 2;
    }

    // ------------------------------------------------------------------ 渲染

    /// <summary>
    /// 把 <see cref="ToolbarTools.Items"/> 渲染成按钮，并把<b>当前选中的那项</b>落到画布上。
    /// <para>
    /// 结构签名没变就<b>只刷新外观</b>：拖粗细滑杆时每一拍都会走到这里，
    /// 而重建控件会顺手把键盘焦点丢掉 —— 表现是"拖着拖着焦点跳走了"。
    /// </para>
    /// <para>
    /// <b>最后那一步"落到画布"是这次修的缺陷</b>：数据模型改了、按钮图标刷了，
    /// 但没有人把新值写进引擎 —— 于是菜单里拖粗细毫无反应，要切到别的工具再切回来
    /// 才看得到（选中时会走一遍应用）。改工具数据必须<b>当场</b>落到画布，这是"改了马上看得见"的那条路。
    /// </para>
    /// </summary>
    private void SyncToolControls()
    {
        var signature = string.Join('|', ToolbarTools.Items.Select(static tool => $"{tool.Id}:{(int)tool.Kind}"));
        if (!string.Equals(signature, _toolSignature, StringComparison.Ordinal))
        {
            BuildToolControls();
        }
        else
        {
            foreach (var tool in ToolbarTools.Items)
            {
                if (_toolControls.TryGetValue(tool.Id, out var control)) RefreshToolVisual(tool, control);
            }

            SyncCheckedTool();
        }

        SyncSelectedToolToCanvas();
        SyncPenSecondaryMenu();
        SyncEraserSecondaryMenu();
    }

    /// <summary>
    /// 把当前选中的工具的数据应用到画布。<b>这条是"改了马上看得见"</b> ——
    /// 菜单里拖粗细、换颜色、改擦法与半径，全都经它落到引擎。
    /// <para>
    /// 只在画布已经在屏时做：画布不在屏（鼠标模式）时写了也是白写，
    /// 真正显示画布的那条路（<see cref="SyncAnnotationOverlay"/>）会带着最新数据走一遍。
    /// </para>
    /// </summary>
    private void SyncSelectedToolToCanvas()
    {
        if (ActiveSurface is not { } surface) return;
        if (!_canvasPresented && !_whiteboardPresented && !_imageViewerPresented) return;
        if (ToolbarTools.Selected is not { } tool) return;

        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
                ApplyPenTool(surface, tool);
                break;
            case ToolbarToolKind.Eraser:
                ApplyEraserTool(surface, tool);
                break;
        }
    }

    private void BuildToolControls()
    {
        _toolSignature = string.Join('|', ToolbarTools.Items.Select(static tool => $"{tool.Id}:{(int)tool.Kind}"));
        _toolControls.Clear();
        _toolOrder.Clear();
        ToolsPanel!.Children.Clear();

        foreach (var tool in ToolbarTools.Items)
        {
            var control = BuildToolControl(tool);
            _toolControls[tool.Id] = control;
            _toolOrder.Add(control);
            ToolsPanel.Children.Add(control);
        }

        FitSizeToContent();
        SyncCheckedTool();
        SyncSelectedToolToCanvas();
        SyncPenSecondaryMenu();
        SyncEraserSecondaryMenu();
    }

    /// <summary>
    /// 一颗按钮。三个绘制工具用 <see cref="RadioToolToggleButton"/>（互斥、且"再点一次"要能开二级菜单），
    /// 其余用普通 <see cref="Button"/>。
    /// <para>
    /// 样式一律<b>具名点键</b>：隐式样式按精确类型查，而 <see cref="RadioToolToggleButton"/> 是
    /// <c>ToggleButton</c> 的派生类型，不保证命中（这条在 FluentJalium 换层时实测过，写在那里 §1）。
    /// </para>
    /// </summary>
    private FrameworkElement BuildToolControl(ToolbarTool tool)
    {
        if (tool.Kind == ToolbarToolKind.Separator)
        {
            var separator = ToolbarToolVisuals.Separator();
            AutomationProperties.SetName(separator, tool.Name);
            return separator;
        }

        if (tool.Kind is ToolbarToolKind.Mouse or ToolbarToolKind.Pen or ToolbarToolKind.Eraser)
        {
            var button = new RadioToolToggleButton { Margin = new Thickness(2, 0, 2, 0) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggleButtonStyle");
            RefreshToolVisual(tool, button);
            button.Checked += (_, _) => OnToolChecked(tool.Id);
            button.Reactivated += (_, _) => OnToolReactivated(tool.Id);
            button.PreviewKeyDown += Tool_OnPreviewKeyDown;
            return button;
        }

        var action = new Button { Margin = new Thickness(2, 0, 2, 0) };
        action.SetResourceReference(FrameworkElement.StyleProperty, "ToolActionButtonStyle");
        RefreshToolVisual(tool, action);
        action.PreviewKeyDown += Tool_OnPreviewKeyDown;

        switch (tool.Kind)
        {
            case ToolbarToolKind.Undo:
                action.Click += (_, _) => { ActiveSurface?.Undo(); };
                break;
            case ToolbarToolKind.Redo:
                action.Click += (_, _) => { ActiveSurface?.Redo(); };
                break;
            case ToolbarToolKind.Settings:
                action.Click += SettingsToolbarButton_OnClick;
                break;
            case ToolbarToolKind.Whiteboard:
                action.Click += (_, _) => ToggleCanvasScene(CanvasScene.Whiteboard);
                break;
            case ToolbarToolKind.Image:
                action.Click += (_, _) => EnterImageCanvas();
                break;
            case ToolbarToolKind.Pdf:
                action.Click += (_, _) => EnterPdfCanvas();
                break;
        }

        if (tool.Kind is ToolbarToolKind.Undo or ToolbarToolKind.Redo) SyncUndoRedoState();
        return action;
    }

    /// <summary>
    /// 刷新一颗按钮的图标 / 色标 / 名称。
    /// <para>
    /// <b>图标不设本地 <c>Foreground</c></b>：颜色靠 ContentPresenter 从控件前景继承，
    /// 一设就把它钉死，选中态"白字在 accent 上"当场失效（库的 ToggleButton 色阶靠这条）。
    /// 所以"这支笔是什么颜色"由色标表达，而不是给图标上色 —— 那条色标与图标长什么样
    /// 都归 <see cref="ToolbarToolVisuals"/>，这里只负责把它塞进去。
    /// </para>
    /// </summary>
    private static void RefreshToolVisual(ToolbarTool tool, FrameworkElement control)
    {
        // 只有内容控件吃 Content；分隔线那种 Decorator 没有它，也就没有图标可刷。
        if (control is ContentControl content)
        {
            content.Content = ToolbarToolVisuals.BuildContent(tool, 40);
            if (content.Content is Grid grid)
            {
                var icon = grid.Children.OfType<FontIcon>().FirstOrDefault();
                if (icon is not null) IconInk.Apply(content, icon);
            }
        }

        var description = ToolbarTools.Describe(tool);
        // 名字与图标都是"此刻"的：同一颗鼠标钮在白板里念作「选择」（呈现，不改存档里的 Name）。
        var display = ToolbarToolVisuals.DisplayName(tool);
        AutomationProperties.SetName(control, $"{display}：{description}");
    }

    /// <summary>把选中态刷到按钮上（互斥：绘制工具里只有一个选中）。</summary>
    private void SyncCheckedTool()
    {
        var selectedId = ToolbarTools.SelectedId;
        _toolSync = true;
        try
        {
            foreach (var tool in ToolbarTools.Items)
            {
                if (!_toolControls.TryGetValue(tool.Id, out var control)) continue;
                if (control is not Jalium.UI.Controls.Primitives.ToggleButton toggle) continue;
                toggle.IsChecked = string.Equals(tool.Id, selectedId, StringComparison.Ordinal);
            }
        }
        finally { _toolSync = false; }

        SyncUndoRedoState();
    }

    /// <summary>
    /// 文档变了 → 刷撤销/重做。<b>这里必须排队一拍，不能当场读</b>。
    /// <para>
    /// 引擎的次序是"先通知、后记账"：<c>InkDocument.Commit</c> 里 <c>RaiseChanged</c> 在
    /// <c>Record</c> 之前（:172 与 :175），<c>Remove</c> 与 <c>Clear</c> 同形。
    /// 当场读 <c>CanUndo</c> 读到的永远是"还差这一笔"的那一瞬 ——
    /// 症状很具体：<b>写完第一笔，撤销钮还是灰的，写第二笔时才亮</b>。
    /// 这一条是验收里直接往文档落笔时抓出来的（<c>CheckWhiteboardUndoLands</c>）。
    /// </para>
    /// <para>同一个"下一拍才落地"的形状在 <c>CanvasSurface</c> 的笔锋注入那边也有，理由一致。</para>
    /// </summary>
    private void OnHistoryStateChanged()
    {
        Dispatcher.BeginInvoke(SyncUndoRedoState);
    }

    /// <summary>
    /// 撤销/重做的可用性只读引擎的账（<c>InkHistory.CanUndo/CanRedo</c>），本端不再自己数笔数。
    /// 画布还没建起来时没有历史可谈，两个按钮都 disabled。
    /// <para>读的是 <see cref="ActiveSurface"/> —— 眼前那块画布的账。</para>
    /// </summary>
    private void SyncUndoRedoState()
    {
        // 撤销/重做的可用性只读引擎的账，读的还是<b>眼前那块画布</b>的那本账。
        var surface = ActiveSurface;
        var canUndo = surface?.CanUndo == true;
        var canRedo = surface?.CanRedo == true;

        foreach (var tool in ToolbarTools.Items)
        {
            if (tool.Kind is not (ToolbarToolKind.Undo or ToolbarToolKind.Redo)) continue;
            if (!_toolControls.TryGetValue(tool.Id, out var control)) continue;
            control.IsEnabled = tool.Kind == ToolbarToolKind.Undo ? canUndo : canRedo;
        }
    }

    private string FirstToolId(ToolbarToolKind kind)
    {
        foreach (var tool in ToolbarTools.Items)
        {
            if (tool.Kind == kind) return tool.Id;
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ 选中与键盘

    private void OnToolChecked(string toolId)
    {
        if (_toolSync) return;
        ToolbarTools.Select(toolId);
    }

    /// <summary>再点一次已选中的那颗钮 = 打开它的二级菜单。系统钮没有二级菜单，忽略。</summary>
    private void OnToolReactivated(string toolId)
    {
        switch (ToolbarTools.Find(toolId)?.Kind)
        {
            case ToolbarToolKind.Pen:
                TogglePenSecondaryMenu();
                break;
            case ToolbarToolKind.Eraser:
                ToggleEraserSecondaryMenu();
                break;
        }
    }

    private void OnToolSelectionChanged()
    {
        SyncCheckedTool();
        SyncCanvasOverlay();
    }

    /// <summary>
    /// 键盘：左右在同一条工具栏里移动，Home/End 到两头；在笔 / 橡皮上下方向键展开二级菜单
    /// （与"再点一次"同一件事，只是给键盘用）。
    /// </summary>
    private void Tool_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardModifiers is ModifierKeys.None or ModifierKeys.Alt && e.Key == Key.Down)
        {
            var tool = ToolbarTools.Find(ToolIdOf(sender));
            ToolbarTools.Select(tool?.Id);
            if (tool is { Kind: ToolbarToolKind.Pen })
            {
                ShowPenSecondaryMenu(focus: true);
                e.Handled = true;
                return;
            }
            if (tool is { Kind: ToolbarToolKind.Eraser })
            {
                ShowEraserSecondaryMenu(focus: true);
                e.Handled = true;
                return;
            }
        }

        if (e.KeyboardModifiers != ModifierKeys.None) return;
        if (sender is not FrameworkElement control) return;

        var current = _toolOrder.IndexOf(control);
        if (current < 0) return;

        var next = e.Key switch
        {
            Key.Right => (current + 1) % _toolOrder.Count,
            Key.Left => (current + _toolOrder.Count - 1) % _toolOrder.Count,
            Key.Home => 0,
            Key.End => _toolOrder.Count - 1,
            _ => -1,
        };
        if (next < 0) return;

        _toolOrder[next].Focus();
        e.Handled = true;
    }

    private string ToolIdOf(object? sender)
    {
        if (sender is not FrameworkElement control) return string.Empty;
        foreach (var (id, candidate) in _toolControls)
        {
            if (ReferenceEquals(candidate, control)) return id;
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ 画布

    private void EnsureAnnotationOverlay()
    {
        if (_annotationOverlay is not null) return;
        _annotationOverlay = new AnnotationOverlayWindow();
        _annotationOverlay.PreviewPointerDown += (_, _) =>
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
        };
        _annotationOverlay.Surface.HistoryStateChanged += OnHistoryStateChanged;
    }

    private void DisposeAnnotationOverlay()
    {
        if (_annotationOverlay is null)
            return;
        _annotationOverlay.Surface.HistoryStateChanged -= OnHistoryStateChanged;
        _annotationOverlay.Close();
        _annotationOverlay = null;
        _canvasPresented = false;
        SyncUndoRedoState();
    }

    // ------------------------------------------------------------ 白板那块画布
    //
    // 与批注那一对（Ensure / Dispose / Present / Conceal）同形，但刻意<b>不共用状态</b>：
    // 两块画布各有各的"在不在屏上"，共用一个布尔的话，切场景时一边隐藏会把另一边的一起抹掉，
    // 而"该截屏的时候没截"这类判断正是读这个布尔读出来的。

    private void EnsureWhiteboard()
    {
        if (_whiteboard is not null) return;
        _whiteboard = new WhiteboardWindow();
        _whiteboard.PreviewPointerDown += (_, _) =>
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
        };
        _whiteboard.HistoryStateChanged += OnHistoryStateChanged;
        _whiteboard.ActivePageChanged += OnWhiteboardActivePageChanged;
    }

    private void DisposeWhiteboard()
    {
        if (_whiteboard is null) return;
        _whiteboard.HistoryStateChanged -= OnHistoryStateChanged;
        _whiteboard.ActivePageChanged -= OnWhiteboardActivePageChanged;
        _whiteboard.Close();
        _whiteboard = null;
        _whiteboardPresented = false;
        SyncUndoRedoState();
    }

    private void PresentWhiteboard()
    {
        if (_whiteboard is null) return;
        _whiteboard.Show();
        _whiteboardPresented = true;
    }

    private void ConcealWhiteboard()
    {
        if (_whiteboard is null) return;
        _whiteboard.Hide();
        _whiteboardPresented = false;
    }

    // ------------------------------------------------------------------ 图片批注

    /// <summary>
    /// 点工具栏那颗「图片」：进图片批注这一块。
    /// <para>
    /// 它<b>不</b>走 <see cref="ToggleCanvasScene"/>：那颗钮连按两次的语义是"进去 / 出来"，
    /// 而图片这块第一次进去必然还要挑一个文件，多出来的一步就是那个文件选择框 ——
    /// 把它藏进"第一次点"里，用户看到的就是"我点了图片，弹了个选文件的框"，这正是要的那一步。
    /// 已经有一张图在页里时，再点就只是回到这一块（不来回跳），免得误点第二次就把窗口关了。
    /// </para>
    /// </summary>
    private void EnterImageCanvas()
    {
        var already = CanvasSceneState.IsActive(CanvasScene.ImageCanvas) && _imageViewer is { PageCount: > 1 };
        if (already)
        {
            PresentImageViewer();
            return;
        }

        ToggleCanvasScene(CanvasScene.ImageCanvas);
    }

    private void EnsureImageViewer()
    {
        if (_imageViewer is not null) return;
        _imageViewer = new ImageViewerWindow();
        _imageViewer.PreviewPointerDown += (_, _) =>
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
        };
        _imageViewer.HistoryStateChanged += OnHistoryStateChanged;
        _imageViewer.ActivePageChanged += OnImageViewerActivePageChanged;

        // 兜底：图片窗口<b>自己</b>被关掉时（X、Alt+F4、任务栏）也必须把批注栏摘回来。
        // 只在 DisposeImageViewer 里摘是不够的 —— 那是我们的正常出口，
        // 而这条是「窗口从视觉树上消失」那一刻：那一刻批注栏还在它里面，
        // 跟着一起没了，而批注栏是 app.MainWindow，整条工具栏就此消失。
        _imageViewer.Closed += (_, _) => RestoreFromHost();
    }

    private void DisposeImageViewer()
    {
        if (_imageViewer is null) return;

        // 先把批注栏摘出来再关图片窗口。顺序反了会怎样：图片窗口一关，
        // 那棵还在它视觉树里的批注栏跟着没了，而 RestoreFromHost 之后再没人调得到 —— 工具栏整条消失。
        RestoreFromHost();
        _imageViewer.HistoryStateChanged -= OnHistoryStateChanged;
        _imageViewer.ActivePageChanged -= OnImageViewerActivePageChanged;
        _imageViewer.Close();
        _imageViewer = null;
        _imageViewerPresented = false;
        SyncUndoRedoState();
    }

    private void PresentImageViewer()
    {
        EnsureImageViewer();
        if (_imageViewer is null) return;
        var mode = AppPreferences.Current.ImageOpenMode;
        _imageViewer.ApplyOpenMode(mode);

        // 已经在屏就别再 Show —— 这是丢最大化的<b>另一条独立的路</b>。
        // Show 的语义是"从没有到有"，对一个已经显形的窗口再走一遍会让外壳把状态重摆一次。
        // Activate 不一样，它只是把它叫到前面，切工具时该有。
        if (!_imageViewerPresented) _imageViewer.Show();
        _imageViewer.Activate();
        _imageViewerPresented = true;
        _imageViewer.RestoreRecentImages();

        // 这里<b>不</b>决定批注栏搬不搬 —— 那是 ApplyToolbarHosting 的活，且只该有它一个。
    }

    private void ConcealImageViewer()
    {
        if (_imageViewer is null) return;
        _imageViewer.Hide();
        _imageViewerPresented = false;
        // 同上：搬回去也不归这里管。放在这里曾经造成"白板那条分支没人搬"，
        // 而隐藏图片窗口和搬回工具栏是两件独立的事，谁触发都不该顺手负责另一件。
    }

    /// <summary>
    /// 图片这一套：<b>没有穿透、没有冻结</b>（底下是一张固定的图，没有"透出去"与"冻住"这两种状态），
    /// 但<b>有选择</b>——「鼠标」那一档在这里是"选择这一页上的墨迹"，与白板同义。
    /// </summary>
    private void SyncImageViewerOverlay()
    {
        if (_isClosing) return;
        if (_settingsWindow is not null)
        {
            ConcealImageViewer();
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            return;
        }

        var tool = ToolbarTools.Selected;
        if (tool is null || tool.Kind == ToolbarToolKind.Mouse)
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            EnsureImageViewer();
            _imageViewer!.Surface.SetSelectMode(true);
            PresentImageViewer();
            SyncUndoRedoState();
            KeepToolbarForeground();
            return;
        }

        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
                HideEraserSecondaryMenu();
                EnsureImageViewer();
                _imageViewer!.Surface.SetSelectMode(false);
                _imageViewer.Surface.SetInkMode();
                ApplyPenTool(_imageViewer.Surface, tool);
                PresentImageViewer();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            case ToolbarToolKind.Eraser:
                HidePenSecondaryMenu();
                EnsureImageViewer();
                _imageViewer!.Surface.SetSelectMode(false);
                _imageViewer.Surface.SetEraseMode();
                ApplyEraserTool(_imageViewer.Surface, tool);
                PresentImageViewer();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            default:
                SyncUndoRedoState();
                break;
        }
    }

    private void OnImageViewerActivePageChanged()
    {
        if (_isClosing || _imageViewer is null) return;
        if (ToolbarTools.Selected is not { } tool)
        {
            SyncUndoRedoState();
            return;
        }

        switch (tool.Kind)
        {
            case ToolbarToolKind.Mouse:
                _imageViewer.Surface.SetSelectMode(true);
                break;
            case ToolbarToolKind.Pen:
                _imageViewer.Surface.SetSelectMode(false);
                _imageViewer.Surface.SetInkMode();
                ApplyPenTool(_imageViewer.Surface, tool);
                break;
            case ToolbarToolKind.Eraser:
                _imageViewer.Surface.SetSelectMode(false);
                _imageViewer.Surface.SetEraseMode();
                ApplyEraserTool(_imageViewer.Surface, tool);
                break;
        }

        SyncUndoRedoState();
        KeepToolbarForeground();
    }

    /// <summary>
    /// 换了一块画布。<b>一边让开、另一边按当前选中项重新决定要不要显形</b>，
    /// 顺带把按钮的外观刷一遍（"选择"那颗的图标与说明是按场景的）。
    /// <para>
    /// 这里只刷外观、不重建控件：重建会丢掉键盘焦点（拖滑杆每一步都会走到重建那条路上，
    /// 那条判断在 <see cref="SyncToolControls"/> 的结构签名里）。
    /// </para>
    /// </summary>
    private void OnCanvasSceneChanged(CanvasScene scene)
    {
        if (_isClosing) return;

        if (!CanvasSceneState.IsPageScene(scene) && ToolbarTools.Selected?.Kind != ToolbarToolKind.Mouse)
        {
            var mouse = ToolbarTools.Items.FirstOrDefault(static item => item.Kind == ToolbarToolKind.Mouse);
            if (mouse is not null) ToolbarTools.Select(mouse.Id);
        }

        // 其余场景一律让开：两块全屏画布叠着没有意义，而"墨迹在各自的历史里"这件事不受影响 ——
        // 隐藏不等于销毁，回来时都还是自己那一屏。
        _annotationOverlay?.SetClickThrough(false);
        ConcealCanvas();
        ConcealWhiteboard();
        ConcealImageViewer();

        RefreshToolVisuals();
        SyncCanvasOverlay();
    }

    /// <summary>
    /// 只刷每颗按钮的外观（图标 / 色标 / 名称 / 说明）。
    /// <para>
    /// <b>换场景为什么要走这一趟而不是重建</b>：按钮的身份是"那一项"，场景改的是"它此刻是什么意思"。
    /// 重建会连控件一起换掉，键盘焦点当场没了 —— 而这正是结构签名 <c>{Id}:{Kind}</c>
    /// 不含场景的那个理由：结构没变，变的只是画上去的样子。
    /// </para>
    /// </summary>
    private void RefreshToolVisuals()
    {
        foreach (var tool in ToolbarTools.Items)
        {
            if (_toolControls.TryGetValue(tool.Id, out var control)) RefreshToolVisual(tool, control);
        }
    }

    /// <summary>
    /// <b>当前那块画布</b>该怎么样：白板在眼前就走白板那套，否则走屏幕批注那套。
    /// <para>
    /// 两条路都是"必经之路"——选中项变了、画布设置变了、场景变了、启动时都走这里。
    /// 场景这一层放在外面而不是塞进 <see cref="SyncAnnotationOverlay"/> 的分支里，
    /// 是因为两边的"鼠标那颗钮"意思根本不同（批注里是"把桌面还回去"，白板里是"选择"），
    /// 混在一个 switch 里迟早写成一堆 <c>if (白板)</c>。
    /// </para>
    /// </summary>
    private void SyncCanvasOverlay()
    {
        if (_isClosing) return;

        switch (CanvasSceneState.Active)
        {
            case CanvasScene.Whiteboard:
                SyncWhiteboardOverlay();
                break;
            case CanvasScene.ImageCanvas:
                SyncImageViewerOverlay();
                break;
            case CanvasScene.PdfCanvas:
                SyncPdfOverlay();
                break;
            default:
                SyncAnnotationOverlay();
                break;
        }

        ApplyToolbarHosting();
    }

    /// <summary>
    /// PDF 那块画布的显隐。<b>与其他两块不同：它由"有没有打开过一份 PDF"决定，不由工具决定</b>。
    /// <para>
    /// 白板与图片是"点一下进、再点一下回"，而 PDF 是"点一下弹文件框、选一份就开窗" ——
    /// 用户没选文件时<b>不该开一个空窗口</b>（那是个没有内容的顶层窗口，
    /// 用户只能关掉它，白白多一次点击），而选完之后就该一直在，
    /// 直到用户明确关掉它或者切去别的画布。
    /// </para>
    /// </summary>
    private void SyncPdfOverlay()
    {
        if (_settingsWindow is not null)
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            SyncUndoRedoState();
            return;
        }

        if (_pdfViewer is null || !_pdfViewer.HasDocument)
        {
            // 没有文档就别占屏：这时"鼠标模式"该做的事是让桌面可用。
            if (_annotationOverlay is not null) _annotationOverlay.Hide();
            _pdfViewerPresented = false;
            SyncUndoRedoState();
            return;
        }

        if (!_pdfViewerPresented)
        {
            _pdfViewerPresented = true;
            _pdfViewer.Show();
        }

        _pdfViewer.Activate();
        SyncUndoRedoState();
    }

    /// <summary>
    /// 工具栏那顆「PDF」：没开过就问一份文件，开过就退回去。
    /// <para>
    /// <b>它是一个 toggle，与图片那颗同形</b>：点第二下是"我要回去用白板/桌面"，
    /// 而不是"再开一份"。一份 PDF 一个窗口是刻意的 —— 两份 PDF 的页码、撤销账、
    /// 渲染档位缓存全是各自一份，塞进同一个窗口会立刻开始互相串。
    /// 想换一份就关掉这个窗口（或者用系统菜单）再点这颗。
    /// </para>
    /// </summary>
    private void EnterPdfCanvas()
    {
        if (_pdfViewer is { HasDocument: true } && _pdfViewerPresented)
        {
            ToggleCanvasScene(CanvasScene.ScreenAnnotation);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "打开 PDF",
            Filter = "PDF 文档|*.pdf",
            CheckFileExists = true,
        };
        var remembered = AppPreferences.Current.LastPdfDirectory;
        if (!string.IsNullOrEmpty(remembered) && Directory.Exists(remembered)) dialog.InitialDirectory = remembered;

        // 取消 = 什么都不发生（而不是退回去）：用户是"还没决定"，不是"决定不打开"。
        if (dialog.ShowDialog((Window?)_pdfViewer ?? this) != true) return;

        EnsurePdfViewer();
        var viewer = _pdfViewer;
        if (viewer is null)
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            MessageBox.Show(this, "PDF 窗口没能建立。", "PDF 批注", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!viewer.TryOpenPdf(dialog.FileName, out var error))
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            MessageBox.Show(this, error ?? "这份 PDF 打不开。", "PDF 批注", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RememberPdfOpened(dialog.FileName);
        _pdfViewerPresented = true;
        CanvasSceneState.Active = CanvasScene.PdfCanvas;
        SyncCanvasOverlay();
    }

    /// <summary>
    /// 启动时开一份 PDF（文件关联那条路）。<b>不在这里弹文件框</b> ——
    /// 用户已经双击过某一个具体文件了，再问一次"你开哪份"是把他的选择当没听见。
    /// </summary>
    internal void BeginStartupPdf(string path)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            EnsurePdfViewer();
            var viewer = _pdfViewer;
            if (viewer is null) return;

            if (!viewer.TryOpenPdf(path, out var error))
            {
                HidePenSecondaryMenu();
                HideEraserSecondaryMenu();
                MessageBox.Show(this, error ?? "这份 PDF 打不开。", "PDF 批注", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RememberPdfOpened(path);
            _pdfViewerPresented = true;
            CanvasSceneState.Active = CanvasScene.PdfCanvas;
            SyncCanvasOverlay();
        }));
    }

    private void RememberPdfOpened(string path)
    {
        var directory = Path.GetDirectoryName(path);
        AppPreferences.Update(AppPreferences.Current with
        {
            LastPdfDirectory = string.IsNullOrEmpty(directory) ? AppPreferences.Current.LastPdfDirectory : directory,
        });
    }

    private void EnsurePdfViewer()
    {
        if (_pdfViewer is not null) return;
        _pdfViewer = new PdfViewerWindow();
        _pdfViewer.PreviewPointerDown += (_, _) =>
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
        };
        _pdfViewer.HistoryStateChanged += OnHistoryStateChanged;
        // 关掉 PDF 窗口必须把批注栏搬回来，否则它留在一个已经没了的窗口里 ——
        // 症状与图片窗口那条一模一样：屏幕上什么都没有，而它又是 app.MainWindow。
        _pdfViewer.Closed += (_, _) => RestoreFromHost();
    }

    /// <summary>
    /// 批注栏<b>该待在哪儿</b> —— 全应用<b>只有这一处</b>决定这件事。
    /// <para>
    /// 这条是踩过一次坑才写成这样的：原先"搬进图片窗口"挂在 <c>PresentImageViewer</c> 上，
    /// "搬回来"散落在几个分支里。结果用户<b>在图片窗口里点白板</b>时走的是白板那条分支 ——
    /// 没人负责搬回来，批注栏就留在一个已经隐藏的图片窗口里，屏幕上什么都没有，
    /// 而它又是 <c>app.MainWindow</c>，再关掉图片窗口就整条工具栏一起没了。
    /// <para>
    /// 教训不是"少调了一个函数"，而是<b>位置不能由事件推动</b>。
    /// 每条分支各自记得收拾，漏一条就是一个"东西消失了但没有任何报错"的状态；
    /// 而"此刻该在哪儿"本来就是当前状态的一个函数，算一次就够。
    /// </para>
    /// <para>幂等：状态没变时它什么都不做，所以可以随便多调。</para>
    /// </summary>
    private void ApplyToolbarHosting()
    {
        var wantsImageWindow = _imageViewerPresented
            && _imageViewer is not null
            && AppPreferences.Current.ImageOpenMode == ImageOpenMode.Window;

        // PDF 窗口**永远**接着批注栏：它没有全屏那一档，
        // 而一个接不到批注栏的 PDF 窗口就是"看得见但写不了"，
        // 那是比"没有这个功能"更糟的一种半成品。
        if (_pdfViewerPresented && _pdfViewer is { HasDocument: true } pdf) RehostInto(pdf.ToolbarHost, pdf);
        else if (wantsImageWindow && _imageViewer is { } viewer) RehostInto(viewer.ToolbarHost, viewer);
        else RestoreFromHost();
    }

    /// <summary>
    /// 白板这一套：<b>没有穿透、没有冻结</b>（那就是一块盖住桌面的底，没有"透出去"这回事）。
    /// <para>
    /// 「选择」（现在还是鼠标那颗的位子）暂时收起白板 —— 等它真的会选笔迹了，
    /// 它在白板里的意思就换成"留下这块底、只是不写"。
    /// </para>
    /// </summary>
    private void SyncWhiteboardOverlay()
    {
        if (_settingsWindow is not null)
        {
            // 设置开着时白板<b>留在屏上</b>：底色那一项是当场生效的，
            // 藏起来的话用户换了档却什么都看不见，那这条设置就等于没做。
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            SyncUndoRedoState();
            return;
        }

        var tool = ToolbarTools.Selected;
        if (tool is null)
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            ConcealWhiteboard();
            SyncUndoRedoState();
            return;
        }

        if (tool.Kind == ToolbarToolKind.Mouse)
        {
            // 「选择」<b>不收起白板</b>：这一档的意思就是"留在这块底上，只是不写"。
            // 引擎的编辑模式交给白板自己（None = 输入归宿主），窗口那边才有挑与挪的那一路。
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            EnsureWhiteboard();
            _whiteboard!.Surface.SetSelectMode(true);
            PresentWhiteboard();
            SyncUndoRedoState();
            KeepToolbarForeground();
            return;
        }

        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
                HideEraserSecondaryMenu();
                EnsureWhiteboard();
                _whiteboard!.Surface.SetSelectMode(false);
                _whiteboard.Surface.SetInkMode();
                ApplyPenTool(_whiteboard.Surface, tool);
                PresentWhiteboard();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            case ToolbarToolKind.Eraser:
                HidePenSecondaryMenu();
                EnsureWhiteboard();
                _whiteboard!.Surface.SetSelectMode(false);
                _whiteboard.Surface.SetEraseMode();
                ApplyEraserTool(_whiteboard.Surface, tool);
                PresentWhiteboard();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            default:
                // 撤销 / 重做 / 分隔线 / 白板 / 设置不是"工具"：它们不改变当前在写还是在擦。
                SyncUndoRedoState();
                break;
        }
    }

    private void OnWhiteboardActivePageChanged()
    {
        if (_isClosing || _whiteboard is null) return;
        if (ToolbarTools.Selected is not { } tool)
        {
            SyncUndoRedoState();
            return;
        }

        switch (tool.Kind)
        {
            case ToolbarToolKind.Mouse:
                _whiteboard.Surface.SetSelectMode(true);
                break;
            case ToolbarToolKind.Pen:
                _whiteboard.Surface.SetSelectMode(false);
                _whiteboard.Surface.SetInkMode();
                ApplyPenTool(_whiteboard.Surface, tool);
                break;
            case ToolbarToolKind.Eraser:
                _whiteboard.Surface.SetSelectMode(false);
                _whiteboard.Surface.SetEraseMode();
                ApplyEraserTool(_whiteboard.Surface, tool);
                break;
        }

        SyncUndoRedoState();
        KeepToolbarForeground();
    }

    /// <summary>
    /// 切场景。<b>已经在这一块时就退回屏幕批注</b> —— 再点一次那颗钮要走出去，而不是"没反应"。
    /// <para>
    /// 三个场景共用这一条，不是因为"三"是个数，而是因为「同一条进、同一条出」是那几颗钮的契约：
    /// 白板点一次进、再点一次回批注；图片也一样。加场景时这里不用改，只在下面那份名单里加成员。
    /// </para>
    /// </summary>
    private void ToggleCanvasScene(CanvasScene scene)
    {
        var target = CanvasSceneState.Active == scene
            ? CanvasScene.ScreenAnnotation
            : scene;

        if (CanvasSceneState.Active == scene && CanvasSceneState.IsPageScene(scene))
        {
            var mouse = ToolbarTools.Items.FirstOrDefault(static item => item.Kind == ToolbarToolKind.Mouse);
            if (mouse is not null) ToolbarTools.Select(mouse.Id);
        }

        CanvasSceneState.Active = target;

        // 进"有页面"的那块时如果手里空着（选中的是"鼠标 / 选择"），先递一支笔过去：
        // 用户点入口是要写，不是要看一块空底；点开之后什么都没发生的那种"没反应"最难猜。
        if (CanvasSceneState.IsPageScene(target) && ToolbarTools.Selected?.Kind == ToolbarToolKind.Mouse)
        {
            var firstPen = ToolbarTools.Items
                .FirstOrDefault(static item => item.Kind == ToolbarToolKind.Pen)
                ?? ToolbarTools.Items.FirstOrDefault(static item => item.Kind == ToolbarToolKind.Eraser);
            if (firstPen is not null) ToolbarTools.Select(firstPen.Id);
        }
    }

    /// <summary>
    /// 把"当前选中的那一项"落到画布上：鼠标模式收起画布，笔进书写模式，橡皮进擦除模式。
    /// <para>
    /// 缩进/参数全部来自<b>那一项自己的数据</b> —— 这就是"两个笔按钮数据独立"在行为上的样子：
    /// 点红笔是红的，点蓝笔是蓝的，中间没有一份共享的"当前笔"。
    /// </para>
    /// <para>
    /// 两个画布开关也在这里生效（见 <see cref="CanvasOptions"/>）：穿透模式改的是<b>鼠标模式</b>
    /// 做什么（收起 vs 留着但不接输入），冻结模式改的是<b>进入画布那一刻</b>（先截一张屏当底图）。
    /// 两者都不是"点一下立刻改画面"，所以判断都挂在这条必经之路上。
    /// </para>
    /// </summary>
    private void SyncAnnotationOverlay()
    {
        if (_isClosing) return;
        if (_settingsWindow is not null)
        {
            ConcealCanvas();
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            return;
        }

        var tool = ToolbarTools.Selected;
        if (tool is null || tool.Kind == ToolbarToolKind.Mouse)
        {
            HidePenSecondaryMenu();
            HideEraserSecondaryMenu();
            SyncUndoRedoState();
            SyncMouseModeCanvas();
            return;
        }

        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
                HideEraserSecondaryMenu();
                EnsureAnnotationOverlay();
                _annotationOverlay!.Surface.SetInkMode();
                ApplyPenTool(_annotationOverlay.Surface, tool);
                PrepareCanvasForDrawing();
                PresentCanvas();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            case ToolbarToolKind.Eraser:
                HidePenSecondaryMenu();
                EnsureAnnotationOverlay();
                _annotationOverlay!.Surface.SetEraseMode();
                ApplyEraserTool(_annotationOverlay.Surface, tool);
                PrepareCanvasForDrawing();
                PresentCanvas();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            default:
                // 撤销 / 重做 / 分隔线不是"工具"：它们不改变当前在写还是在擦。
                SyncUndoRedoState();
                break;
        }
    }

    /// <summary>
    /// 画布设置变了。<b>只认屏幕批注那一套</b>：穿透与冻结这两个开关本来就是批注特有的
    /// （白板是一块盖住桌面的底，没有"透出去"与"冻住"这两种状态），
    /// 别的场景改了开关不该让这块画布重排一次。
    /// <para>这道闸原先藏在 <see cref="CanvasOptions.Update"/> 里（"只有当前场景才发通知"），
    /// 现在 <c>Changed</c> 如实报场景，闸就挪到消费这一侧。</para>
    /// </summary>
    private void OnCanvasOptionsChanged(CanvasScene scene)
    {
        if (scene != CanvasScene.ScreenAnnotation) return;
        SyncAnnotationOverlay();
    }

    /// <summary>
    /// 鼠标模式下这块画布怎么办：<b>默认收起来；开了穿透模式就留着</b>，但让它接不住输入，
    /// 于是鼠标与触摸直接落到下面的窗口上 —— 批注留着不动，人继续操作电脑。
    /// <para>
    /// 从没画过东西时不留空画布：那是白白多一个全屏最顶层窗口，而画布是懒创建的（<c>null</c>），
    /// 这里顺势就不建它。
    /// </para>
    /// </summary>
    private void SyncMouseModeCanvas()
    {
        if (_annotationOverlay is null) return;

        _annotationOverlay.Surface.SetInkMode();

        if (!CanvasOptions.For(CanvasScene.ScreenAnnotation).PassThrough)
        {
            _annotationOverlay.SetClickThrough(false);
            ConcealCanvas();
            return;
        }

        // 穿透是在"用电脑"：底图必须让开（如果还挂着的话），否则用户看着一张冻结的旧屏、
        // 点击却落在真实的窗口上。笔记本身留着 —— 那正是穿透模式要留下的东西。
        //
        // 这里同时记一笔"用户离开过书写"：穿透模式下画布留在屏上，
        // 只看 _canvasPresented 的话，"去鼠标模式操作了半天再回来写"与"在画布里换了一下橡皮"
        // 长得一模一样，而这两件事该不该重截屏正好相反。
        _annotationOverlay.SetFrozenBackground(null);
        _leftDrawingSession = true;
        PresentCanvas();

        // 顺序要紧：<c>Show</c> 会让外壳按自己的规则重算扩展样式，先设的穿透位会被抹掉。
        _annotationOverlay.SetClickThrough(true);
    }

    /// <summary>
    /// 进入书写 / 擦除之前的准备。
    /// <para>
    /// <b>冻结底图必须在这里截</b>，也就是<b>在 <c>Show</c> 之前</b>：这一刻屏幕上还没有画布，
    /// 截到的才是"批注之前"的那一屏。放到 Show 之后就是把自己（连同上一轮的笔记）一起截进去。
    /// </para>
    /// </summary>
    private void PrepareCanvasForDrawing()
    {
        if (_annotationOverlay is null) return;

        _annotationOverlay.SetClickThrough(false);

        if (!CanvasOptions.For(CanvasScene.ScreenAnnotation).Freeze)
        {
            // 关掉冻结就把底图撤掉（回到透明看得见真实桌面）；
            // 不撤的话它会一直留着上一张截图，而用户以为自己关掉了。
            _annotationOverlay.SetFrozenBackground(null);
            return;
        }

        // 已经在屏上就不再截：这一屏已经有笔记了，再截一张会把笔记烙进底图里。
        // 冻结是"每次重新进入画布时截一张"，不是"每次切工具都截一张"。
        //
        // 例外是穿透模式：那时画布一直留在屏上（所以 _canvasPresented 一直是真的），
        // 但用户分明离开过书写、去操作了电脑 —— 回到书写时必须重截一张，
        // 否则他会看到一张"冻结"却其实透明的画布，而开关明明开着。
        if (_canvasPresented && !_leftDrawingSession) return;
        _leftDrawingSession = false;

        _annotationOverlay.SetFrozenBackground(
            ScreenCapture.CaptureBehind(_annotationOverlay, this, _penMenuWindow, _eraserMenuWindow));
    }

    private void PresentCanvas()
    {
        if (_annotationOverlay is null) return;
        _annotationOverlay.Show();
        _canvasPresented = true;
    }

    private void ConcealCanvas()
    {
        if (_annotationOverlay is null) return;
        _annotationOverlay.Hide();
        _canvasPresented = false;
    }

    /// <summary>
    /// 画布显形之后把前台还给批注栏。
    /// <para>
    /// 以前这一步同时负责"把工具栏顶到画布之上"（Topmost 关一下再开、再 Activate，
    /// 还排在 Dispatcher 队列末尾又补一遍 —— 因为 Jalium 的透明窗口 Show 时会自己动 native Z 序）。
    /// 那件事现在归 <see cref="WindowLayerManager"/>，而它排 Z 序用的是 <c>SWP_NOACTIVATE</c>、不碰焦点。
    /// 所以这里只剩原来的另一半：让批注栏仍然是前台窗口，键盘快捷键与 Esc 才不会落到别处。
    /// </para>
    /// </summary>
    private void KeepToolbarForeground() => Activate();

    private void ApplyPenTool(CanvasSurface surface, ToolbarTool tool)
    {
        surface.SetPenKind(tool.PenKind);
        surface.SetPenColor(Argb.Unpack(tool.ColorFor(CanvasSceneState.Active)));
        surface.SetPenThickness(tool.Thickness);
    }

    private void ApplyEraserTool(CanvasSurface surface, ToolbarTool tool)
    {
        surface.SetEraserMode(tool.EraseMode);
        surface.SetEraserRadius(tool.EraserRadius);
    }

    // ------------------------------------------------------------------ 二级菜单

    /// <summary>
    /// 二级菜单编辑的是<b>当前选中的那一项</b>，而不是什么全局值。
    /// <para>
    /// 这不需要"记住是哪个按钮打开的菜单"：菜单只能从已选中的那颗钮打开
    /// （<c>Reactivated</c> 只在选中态下才发），所以"选中的那一项"就是它。
    /// </para>
    /// </summary>
    private void EnsurePenSecondaryMenuWindow()
    {
        if (_penMenuWindow is not null) return;

        _penMenuWindow = new PenSecondaryMenuWindow { Owner = this };
        _penMenuWindow.DismissRequested += () => { HidePenSecondaryMenu(); Activate(); FocusSelectedTool(); };
        _penMenuWindow.PenColorChanged += color => ToolbarTools.UpdateSelectedPenColor(Argb.Pack(color));
        _penMenuWindow.PenThicknessChanged += thickness => ToolbarTools.UpdateSelectedPen(tool => tool with { Thickness = thickness });
        _penMenuWindow.PenKindChanged += kind => ToolbarTools.UpdateSelectedPen(tool => tool with { PenKind = kind });
        // 笔锋档位：先让引擎套用那一档（它会发通知），通知再把结果写回这支笔 ——
        // 于是"选了档位"与"这支笔的形状变了"是同一件事，不需要两处各写一遍。
        _penMenuWindow.TipPresetChanged += id => InkTipOptions.SelectPreset(id);
        _penMenuWindow.Closed += (_, _) =>
        {
            _penMenuWindow = null;
            _penMenuVisible = false;
        };

        SyncPenSecondaryMenu();
    }

    private void SyncPenSecondaryMenu()
    {
        if (_penMenuWindow is null) return;
        if (ToolbarTools.Selected is not { Kind: ToolbarToolKind.Pen } pen)
        {
            _penMenuWindow.Hide();
            _penMenuVisible = false;
            return;
        }

        _penMenuWindow.SetCurrentState(Argb.Unpack(pen.ColorFor(CanvasSceneState.Active)), pen.Thickness, pen.PenKind);
    }

    private void SyncEraserSecondaryMenu()
    {
        if (_eraserMenuWindow is null) return;
        if (ToolbarTools.Selected is not { Kind: ToolbarToolKind.Eraser } eraser)
        {
            _eraserMenuWindow.Hide();
            _eraserMenuVisible = false;
            return;
        }

        _eraserMenuWindow.SetCurrentState(eraser.EraseMode, eraser.EraserRadius);
    }

    private void FocusSelectedTool()
    {
        var control = FindToolControl(ToolbarTools.SelectedId);
        control?.Focus();
    }

    private void PositionSecondaryMenus()
    {
        if (_penMenuVisible) PositionPenSecondaryMenu();
        if (_eraserMenuVisible) PositionEraserSecondaryMenu();
    }

    private void PositionPenSecondaryMenu()
    {
        if (_penMenuWindow is null)
            return;

        // 基准是**当前宿主**，不是 this。重父化期间批注栏的视觉在图片窗口里，
        // 而本窗口已经 Hide —— 它的 Left/Top 停在上一次摆位，早就不描述批注栏在哪了。
        var host = _hostWindow ?? this;
        if (!FlyoutPlacement.Position(host, _penMenuWindow))
        {
            _penMenuWindow.Left = host.Left;
            _penMenuWindow.Top = host.Top + host.Height - 8;
        }
    }

    private void ShowPenSecondaryMenu(bool focus = false)
    {
        if (ToolbarTools.Selected is not { Kind: ToolbarToolKind.Pen }) return;

        HideEraserSecondaryMenu();
        EnsurePenSecondaryMenuWindow();
        SyncPenSecondaryMenu();
        PositionPenSecondaryMenu();
        _penMenuWindow!.Show();
        PositionPenSecondaryMenu();
        _penMenuVisible = true;
        FluentThemeManager.Enter(_penMenuWindow.Content as UIElement ?? _penMenuWindow);
        if (focus)
            Dispatcher.BeginInvoke(() =>
            {
                // Run after the queued toolbar Z-order update, so it cannot steal focus back.
                if (_isClosing || !_penMenuVisible || _penMenuWindow is null) return;
                _penMenuWindow.Activate();
                _penMenuWindow.FocusSelectedColor();
            });
    }

    private void HidePenSecondaryMenu()
    {
        _penMenuWindow?.Hide();
        _penMenuVisible = false;
    }

    private void TogglePenSecondaryMenu()
    {
        if (ToolbarTools.Selected is not { Kind: ToolbarToolKind.Pen }) return;

        if (_penMenuWindow is not null && _penMenuVisible)
        {
            _penMenuWindow.Hide();
            _penMenuVisible = false;
            return;
        }

        ShowPenSecondaryMenu();
    }

    private void EnsureEraserSecondaryMenuWindow()
    {
        if (_eraserMenuWindow is not null) return;

        _eraserMenuWindow = new EraserSecondaryMenuWindow { Owner = this };
        _eraserMenuWindow.DismissRequested += () => { HideEraserSecondaryMenu(); Activate(); FocusSelectedTool(); };
        _eraserMenuWindow.EraserModeChanged += mode => ToolbarTools.UpdateSelectedEraser(tool => tool with { EraseMode = mode });
        _eraserMenuWindow.EraserRadiusChanged += radius => ToolbarTools.UpdateSelectedEraser(tool => tool with { EraserRadius = radius });
        _eraserMenuWindow.ClearRequested += () =>
        {
            ActiveSurface?.ClearCanvas();
            SyncUndoRedoState();
        };
        _eraserMenuWindow.Closed += (_, _) =>
        {
            _eraserMenuWindow = null;
            _eraserMenuVisible = false;
        };

        SyncEraserSecondaryMenu();
    }

    private void PositionEraserSecondaryMenu()
    {
        if (_eraserMenuWindow is null)
            return;

        var host = _hostWindow ?? this;
        if (!FlyoutPlacement.Position(host, _eraserMenuWindow))
        {
            _eraserMenuWindow.Left = host.Left;
            _eraserMenuWindow.Top = host.Top + host.Height - 8;
        }
    }

    private void ShowEraserSecondaryMenu(bool focus = false)
    {
        if (ToolbarTools.Selected is not { Kind: ToolbarToolKind.Eraser }) return;

        HidePenSecondaryMenu();
        EnsureEraserSecondaryMenuWindow();
        SyncEraserSecondaryMenu();
        PositionEraserSecondaryMenu();
        _eraserMenuWindow!.Show();
        PositionEraserSecondaryMenu();
        _eraserMenuVisible = true;
        FluentThemeManager.Enter(_eraserMenuWindow.Content as UIElement ?? _eraserMenuWindow);
        if (focus)
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || !_eraserMenuVisible || _eraserMenuWindow is null) return;
                _eraserMenuWindow.Activate();
                _eraserMenuWindow.FocusSelectedMode();
            });
    }

    private void HideEraserSecondaryMenu()
    {
        if (_eraserMenuWindow is null)
            return;

        _eraserMenuWindow.ResetClearConfirmation();
        _eraserMenuWindow.Hide();
        _eraserMenuVisible = false;
    }

    private void ToggleEraserSecondaryMenu()
    {
        if (ToolbarTools.Selected is not { Kind: ToolbarToolKind.Eraser }) return;

        if (_eraserMenuWindow is not null && _eraserMenuVisible)
        {
            HideEraserSecondaryMenu();
            return;
        }

        ShowEraserSecondaryMenu();
    }

    // ------------------------------------------------------------------ 拖动

    private void WireDragHandle()
    {
        var h = DragHandle;
        h.PreviewPointerDown += DragHandle_OnPreviewPointerDown;
        h.PreviewTouchDown += DragHandle_OnPreviewTouchDown;
        h.PreviewTouchMove += DragHandle_OnPreviewTouchMove;
        h.PreviewTouchUp += DragHandle_OnPreviewTouchUp;
        h.LostTouchCapture += DragHandle_OnLostTouchCapture;
    }

    private void DragHandle_OnPreviewPointerDown(object sender, RoutedEventArgs e)
    {
        if (e is not PointerDownEventArgs p || sender is not FrameworkElement fe)
            return;

        if (!p.IsPressed)
            return;

        // 触摸有独立的 Touch 事件和捕获通道，不能调用只支持鼠标左键的 DragMove
        if (p.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            return;

        // 重父化时批注栏是图片窗口里的一个控件，拖手柄就该拖<b>图片窗口</b> ——
        // 两者本就是同一个窗口的同一棵树，不存在"谁跟着谁"的问题。
        // 原生 DragMove 只能拖自己所在的那个窗口，所以要点名宿主去拖。
        // 拖自己那个（此时已经 Hide 的）窗口会没有任何反应。
        if (IsRehosted && _hostWindow is { } host)
        {
            host.DragMove();
        }
        else
        {
            DragMove();
        }

        p.Handled = true;
        PositionPenSecondaryMenu();
    }

    private void DragHandle_OnPreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (sender is not UIElement captureOwner)
            return;

        // A second contact must not fall through into promoted mouse/window dragging.
        e.Handled = true;
        if (_touchDragDevice is not null) return;

        var rootPoint = e.GetTouchPoint(this).Position;

        // 触摸那条路不走 DragMove（它只支持鼠标左键），所以这里要自己记住"是谁在动"。
        // 重父化时被拖的是图片窗口（批注栏就在它里面）。
        _touchDragOwner = IsRehosted && _hostWindow is { } host ? host : this;
        _touchDragDevice = e.TouchDevice;
        _touchDragStartWindowLeft = _touchDragOwner.Left;
        _touchDragStartWindowTop = _touchDragOwner.Top;
        _touchDragStartScreenPoint = new Point(
            _touchDragOwner.Left + rootPoint.X,
            _touchDragOwner.Top + rootPoint.Y);

        // 捕获当前触点，手指即使瞬间移出原来的 40×56 命中区，拖动也不会中断。
        if (!captureOwner.CaptureTouch(e.TouchDevice)) ResetTouchDragState();
    }

    private void DragHandle_OnPreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (_touchDragDevice is null || e.TouchDevice.Id != _touchDragDevice.Id)
            return;

        // TouchPoint.Position 是窗口根坐标。把当前窗口 Left/Top 加回去，
        // 就得到稳定的屏幕坐标；即使窗口已经在上一帧移动，也不会产生反向抖动。
        var rootPoint = e.GetTouchPoint(this).Position;
        var owner = _touchDragOwner ?? this;
        var currentScreenX = owner.Left + rootPoint.X;
        var currentScreenY = owner.Top + rootPoint.Y;

        owner.Left = _touchDragStartWindowLeft
            + (currentScreenX - _touchDragStartScreenPoint.X);
        owner.Top = _touchDragStartWindowTop
            + (currentScreenY - _touchDragStartScreenPoint.Y);

        PositionPenSecondaryMenu();
        e.Handled = true;
    }

    private void DragHandle_OnPreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (_touchDragDevice is null || e.TouchDevice.Id != _touchDragDevice.Id)
            return;

        EndTouchDrag(sender as UIElement);
        e.Handled = true;
    }

    private void DragHandle_OnLostTouchCapture(object sender, TouchEventArgs e)
    {
        if (_touchDragDevice is null || e.TouchDevice.Id != _touchDragDevice.Id)
            return;

        ResetTouchDragState();
    }

    private void EndTouchDrag(UIElement? captureOwner)
    {
        var touchDevice = _touchDragDevice;
        ResetTouchDragState();

        if (captureOwner is not null && touchDevice is not null)
            captureOwner.ReleaseTouchCapture(touchDevice);
    }

    private void ResetTouchDragState()
    {
        _touchDragDevice = null;
        _touchDragOwner = null;
    }

    // ------------------------------------------------------------------ 设置

    private void SettingsToolbarButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var w = new SettingsWindow { Owner = this };
        _settingsWindow = w;

        HidePenSecondaryMenu();
        HideEraserSecondaryMenu();

        // 画布收起，不是为了让设置窗口压得住它 —— 层级已经由 WindowLayerManager 保证
        // （对话框层在画布层之上，两者都可见时也是）。
        // 收起它是为了让<b>桌面可用</b>：画布是一个吃满全屏输入的最顶层窗口，
        // 留着它，别的应用点不动、设置窗口在它上面的那一小块之外也点不动。
        // Hide 是暂停输入，不是销毁 —— 用户已经写下的笔迹一笔不丢，关掉设置就回来。
        ConcealCanvas();

        w.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_isClosing) return;
            SyncCanvasOverlay();
        };

        w.Show();
        w.Activate();
    }

    private void OnPreferencesChanged(PreferenceSnapshot value)
    {
        // 工具数据（颜色 / 粗细 / 笔锋 / 擦法 / 半径）不在这里同步：它们的真相在
        // ToolbarTools 里，设置页改的是<b>那一项</b>，改完会发 LayoutChanged 过来。
        // 这里只剩两条与"当前工具"无关的偏好：层级策略，以及设置页可能把选中项换掉。
        WindowLayerManager.SetPinned(this, value.KeepToolbarOnTop);
        SyncPenSecondaryMenu();
        SyncEraserSecondaryMenu();
    }
}
