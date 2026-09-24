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
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private bool _toolSync;
    private bool _isClosing;
    private AnnotationOverlayWindow? _annotationOverlay;
    private SettingsWindow? _settingsWindow;
    private PenSecondaryMenuWindow? _penMenuWindow;
    private bool _penMenuVisible;
    private EraserSecondaryMenuWindow? _eraserMenuWindow;
    private bool _eraserMenuVisible;
    private TouchDevice? _touchDragDevice;
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

    /// <summary>工具栏上某一项的按钮控件；没有这一项时返回 <c>null</c>。</summary>
    internal FrameworkElement? FindToolControl(string id) => _toolControls.GetValueOrDefault(id);

    /// <summary>按显示顺序的全部按钮控件。</summary>
    internal IReadOnlyList<FrameworkElement> ToolControls => _toolOrder;

    public AnnotationToolbarWindow()
    {
        AllowsTransparency = true;
        InitializeComponent();
        SystemBackdrop = WindowBackdropType.None;
        Background = TransparentBrush;
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
        AppPreferences.Changed += OnPreferencesChanged;
        LocationChanged += (_, _) =>
        {
            if (_penMenuVisible) PositionPenSecondaryMenu();
            if (_eraserMenuVisible) PositionEraserSecondaryMenu();
        };
        Hiding += (_, _) => EndTouchDrag(DragHandle);
        SystemSettingsChanged += (_, _) => FluentThemeManager.RefreshSystemTheme();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_penMenuVisible) HidePenSecondaryMenu();
            else if (_eraserMenuVisible) HideEraserSecondaryMenu();
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
        };

        SyncToolControls();
        SyncAnnotationOverlay();
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
        if (_annotationOverlay is null || !_canvasPresented) return;
        if (ToolbarTools.Selected is not { } tool) return;

        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
                ApplyPenToolToOverlay(tool);
                break;
            case ToolbarToolKind.Eraser:
                ApplyEraserToolToOverlay(tool);
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
                action.Click += (_, _) => { _annotationOverlay?.Undo(); };
                break;
            case ToolbarToolKind.Redo:
                action.Click += (_, _) => { _annotationOverlay?.Redo(); };
                break;
            case ToolbarToolKind.Settings:
                action.Click += SettingsToolbarButton_OnClick;
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
        if (control is ContentControl content) content.Content = ToolbarToolVisuals.BuildContent(tool, 40);

        var description = ToolbarTools.Describe(tool);
        AutomationProperties.SetName(control, $"{tool.Name}：{description}");
        control.ToolTip = tool.Kind switch
        {
            ToolbarToolKind.Pen => $"{tool.Name} · {description}（再点一次打开设置）",
            ToolbarToolKind.Eraser => $"{tool.Name} · {description}（再点一次打开设置）",
            _ => tool.Name,
        };
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
    /// 撤销/重做的可用性只读引擎的账（<c>InkHistory.CanUndo/CanRedo</c>），本端不再自己数笔数。
    /// 画布还没建起来时没有历史可谈，两个按钮都 disabled。
    /// </summary>
    private void SyncUndoRedoState()
    {
        var canUndo = _annotationOverlay?.CanUndo == true;
        var canRedo = _annotationOverlay?.CanRedo == true;

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
        SyncAnnotationOverlay();
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
        _annotationOverlay.HistoryStateChanged += SyncUndoRedoState;
    }

    private void DisposeAnnotationOverlay()
    {
        if (_annotationOverlay is null)
            return;
        _annotationOverlay.HistoryStateChanged -= SyncUndoRedoState;
        _annotationOverlay.Close();
        _annotationOverlay = null;
        _canvasPresented = false;
        SyncUndoRedoState();
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
                _annotationOverlay!.SetInkMode();
                ApplyPenToolToOverlay(tool);
                PrepareCanvasForDrawing();
                PresentCanvas();
                SyncUndoRedoState();
                KeepToolbarForeground();
                break;

            case ToolbarToolKind.Eraser:
                HidePenSecondaryMenu();
                EnsureAnnotationOverlay();
                _annotationOverlay!.SetEraseMode();
                ApplyEraserToolToOverlay(tool);
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

    private void ApplyPenToolToOverlay(ToolbarTool tool)
    {
        if (_annotationOverlay is null) return;
        _annotationOverlay.SetPenKind(tool.PenKind);
        _annotationOverlay.SetPenColor(Argb.Unpack(tool.ColorArgb));
        _annotationOverlay.SetPenThickness(tool.Thickness);
    }

    private void ApplyEraserToolToOverlay(ToolbarTool tool)
    {
        if (_annotationOverlay is null) return;
        _annotationOverlay.SetEraserMode(tool.EraseMode);
        _annotationOverlay.SetEraserRadius(tool.EraserRadius);
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
        _penMenuWindow.PenColorChanged += color => ToolbarTools.UpdateSelectedPen(tool => tool with { ColorArgb = Argb.Pack(color) });
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

        _penMenuWindow.SetCurrentState(Argb.Unpack(pen.ColorArgb), pen.Thickness, pen.PenKind);
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

    private void PositionPenSecondaryMenu()
    {
        if (_penMenuWindow is null)
            return;

        if (!FlyoutPlacement.Position(this, _penMenuWindow))
        {
            _penMenuWindow.Left = Left;
            _penMenuWindow.Top = Top + Height - 8;
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
            _annotationOverlay?.ClearCanvas();
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

        if (!FlyoutPlacement.Position(this, _eraserMenuWindow))
        {
            _eraserMenuWindow.Left = Left;
            _eraserMenuWindow.Top = Top + Height - 8;
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

        // 触摸有独立的 Touch 事件和捕获通道，不能调用只支持鼠标左键的 DragMove。
        if (p.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            return;

        // 鼠标继续使用系统原生 DragMove，获得与普通窗口标题栏一致的拖动体验。
        DragMove();
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
        _touchDragDevice = e.TouchDevice;
        _touchDragStartWindowLeft = Left;
        _touchDragStartWindowTop = Top;
        _touchDragStartScreenPoint = new Point(
            Left + rootPoint.X,
            Top + rootPoint.Y);

        // 捕获当前触点，手指即使瞬间移出原来的 40×56 命中区，拖动也不会中断。
        if (!captureOwner.CaptureTouch(e.TouchDevice)) ResetTouchDragState();
    }

    private void DragHandle_OnPreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (_touchDragDevice is null || e.TouchDevice.Id != _touchDragDevice.Id)
            return;

        // TouchPoint.Position 是窗口根坐标。将当前窗口 Left/Top 加回去，
        // 就得到稳定的屏幕坐标；即使窗口已经在上一帧移动，也不会产生反向抖动。
        var rootPoint = e.GetTouchPoint(this).Position;
        var currentScreenX = Left + rootPoint.X;
        var currentScreenY = Top + rootPoint.Y;

        Left = _touchDragStartWindowLeft
            + (currentScreenX - _touchDragStartScreenPoint.X);
        Top = _touchDragStartWindowTop
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
            SyncAnnotationOverlay();
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
