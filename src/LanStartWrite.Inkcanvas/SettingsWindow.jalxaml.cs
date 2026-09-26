using System.Reflection;
using Dusk.Adapter.Jalium;
using FluentJalium.Controls;
using FluentJalium.Themes;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<SettingsNavPage, FrameworkElement> _pages;
    private readonly Dictionary<SettingsNavPage, FluentNavigationItem> _navigation;
    private readonly StrokeTipEditor _tipEditor;
    private ToolbarLayoutStrip? _layoutStrip;
    private ToolbarToolLibrary? _library;

    private readonly JaliumInkCanvas _tipPreview;

    /// <summary>
    /// 「工具栏」那一页：工具栏此刻有的那些项，一排，可拖动改序、可从组件库拖入新建。
    /// 探针据此按项标识取那一块（元素全是代码建的，没有 <c>x:Name</c>）。
    /// </summary>
    internal ToolbarLayoutStrip ToolLayout =>
        _layoutStrip ?? throw new InvalidOperationException("工具栏那一页还没接上（WireToolbarControls 没跑）");

    /// <summary>「工具菜单」那一页：组件库，一格格可拖出。</summary>
    internal ToolbarToolLibrary ToolLibrary =>
        _library ?? throw new InvalidOperationException("组件库还没接上（WireToolbarControls 没跑）");

    /// <summary>
    /// 探针用：把设置页切到某一页。
    /// <para>
    /// <b>只有当前那一页在视觉树里</b>（见 <c>NavigateTo</c>），所以要量"排了版没有"
    /// 必须先真的切过去 —— 在别的页上量，量到的全是 0，而"全是 0"这件事既可能是
    /// 控件没建，也可能是压根没进树，两者在读数上一模一样。
    /// </para>
    /// </summary>
    internal void GoToPageForProbe(SettingsNavPage page) => NavigateTo(page, force: true);

    /// <summary>
    /// 笔锋档位下拉里每一项对应的档位标识（下标即 <c>ComboBox.Items</c> 的下标）。
    /// <para>
    /// 为什么不用 <c>Tag</c> 挂在 <see cref="ComboBoxItem"/> 上：这个下拉的项是<b>重建</b>出来的，
    /// 一个平行的标识表比"往控件上挂数据"更直白，也让 UiSmoke 能直接对着它断言。
    /// 末尾那一项是空串，代表「自定义」。
    /// </para>
    /// </summary>
    private readonly List<string> _tipPresetIds = [];

    private bool _sync;
    private bool _loaded;
    private SettingsNavPage _page;

    private Grid PageHost => (Grid)SettingsContentHost!;
    private Slider PenWidth => (Slider)PenWidthSlider!;
    private ComboBox ThemeChoice => (ComboBox)ThemeComboBox!;
    private ComboBox TipPreset => (ComboBox)TipPresetComboBox!;

    public SettingsWindow()
    {
        InitializeComponent();

        // 层级登记：对话框层，全应用最高。这条层级保证的是"画布压不住设置窗口" ——
        // 与"设置窗口必须是最前"是两回事：没有画布在场时它就是个普通窗口（不置顶），
        // 可以被压到别的应用后面，这一点由 WindowLayerManager 里"对话框在场即退出置顶带"算出来。
        WindowLayerManager.Register(this, WindowLayer.Dialog, "设置");

        _pages = new()
        {
            [SettingsNavPage.Appearance] = AppearanceSectionPanel!,
            [SettingsNavPage.Ink] = InkSectionPanel!,
            [SettingsNavPage.Canvas] = CanvasSectionPanel!,
            [SettingsNavPage.File] = FileSectionPanel!,
            [SettingsNavPage.Toolbar] = ToolbarSectionPanel!,
            [SettingsNavPage.Interaction] = InteractionSectionPanel!,
            [SettingsNavPage.About] = AboutSectionPanel!,
        };
        _navigation = new()
        {
            [SettingsNavPage.Appearance] = (FluentNavigationItem)AppearanceNavButton!,
            [SettingsNavPage.Ink] = (FluentNavigationItem)InkNavButton!,
            [SettingsNavPage.Canvas] = (FluentNavigationItem)CanvasNavButton!,
            [SettingsNavPage.File] = (FluentNavigationItem)FileNavButton!,
            [SettingsNavPage.Toolbar] = (FluentNavigationItem)ToolbarNavButton!,
            [SettingsNavPage.Interaction] = (FluentNavigationItem)InteractionNavButton!,
            [SettingsNavPage.About] = (FluentNavigationItem)AboutNavButton!,
        };

        // 笔锋面板是"照引擎的参数表生成"的，标记里只有一个空容器 —— 见 StrokeTipEditor。
        _tipEditor = new StrokeTipEditor(InkTipOptions.Settings);
        _tipEditor.Build((StackPanel)TipParameterSections!);

        // 试写区：一台真正的墨迹控件，读的是同一份笔锋设置，因此改参数当场看得见。
        _tipPreview = new JaliumInkCanvas();
        ((Grid)TipPreviewHost!).Children.Add(_tipPreview);
        InkTipOptions.ApplyTo(_tipPreview.TipSettings);

        // 工具栏设置：两个独立的对象（那一排 / 组件库），宿主面板在标记里，见 WireToolbarControls。

        // Only the current page belongs to the live tree: no hidden controls in Tab/UIA.
        PageHost.Children.Clear();
        NavigateTo(SettingsNavPage.Appearance, force: true);

        WireControls();
        Synchronize(AppPreferences.Current);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        ((TextBlock)AboutVersionText!).Text = $"版本 {version?.Major}.{version?.Minor}.{version?.Build} · Jalium.UI 26.10.9";
        UpdateSaveStatus();

        AppPreferences.Changed += Synchronize;
        AppPreferences.SaveStatusChanged += UpdateSaveStatus;
        FluentThemeManager.Changed += OnThemeChanged;
        SystemSettingsChanged += (_, _) => FluentThemeManager.RefreshSystemTheme();
        Loaded += (_, _) =>
        {
            _loaded = true;
            OnThemeChanged();
            ClearVisibleTooltips();
            Dispatcher.BeginInvoke(ClearVisibleTooltips);
        };
        // The content root, unlike the Window's declared Width, follows native client resizing.
        ((FrameworkElement)Content!).SizeChanged += (_, e) =>
            PageHost.Margin = new Thickness(e.NewSize.Width < 640 ? 16 : 24);
        Closed += (_, _) =>
        {
            AppPreferences.Changed -= Synchronize;
            AppPreferences.SaveStatusChanged -= UpdateSaveStatus;
            FluentThemeManager.Changed -= OnThemeChanged;
            InkTipOptions.Changed -= OnTipOptionsChanged;
            InkTipOptions.PresetsChanged -= OnTipPresetsChanged;
            ToolbarTools.LayoutChanged -= OnToolbarToolsChanged;
            ToolbarTools.SelectionChanged -= OnToolbarToolsChanged;
            CanvasOptions.Changed -= SyncCanvasSection;

            // 墨迹控件必须显式拆：Jalium 不代调，而它挂着整棵墨迹视觉树（见 AGENTS）。
            _tipPreview.Dispose();
            AppPreferences.Flush();
        };
    }

    private void WireControls()
    {
        foreach (var (page, item) in _navigation)
        {
            item.ToolTip = null;
            AutomationProperties.SetName(item, (string?)item.Content ?? page.ToString());
        }
        NavigationRoot!.SelectionChanged += OnNavigationSelectionChanged;
        NavigationRoot.SizeChanged += (_, _) => ClearVisibleTooltips();
        BindSwitch((FluentToggleSwitch)ReduceMotionSwitch!, "减少动画", value =>
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = value }));
        BindSwitch((FluentToggleSwitch)KeepToolbarOnTopSwitch!, "始终置顶工具栏", value =>
            AppPreferences.Update(AppPreferences.Current with { KeepToolbarOnTop = value }));
        BindSwitch((FluentToggleSwitch)PressureSwitch!, "压力感应", InkRuntimeOptions.SetEnablePressure);

        // ---- 「文件」这一页 ----
        AutomationProperties.SetName(ImageOpenModeChoice, "图片批注的打开方式");
        ImageOpenModeChoice.Items.Add("窗口");
        ImageOpenModeChoice.Items.Add("全屏");
        ImageOpenModeChoice.SelectionChanged += (_, _) =>
        {
            if (_sync) return;
            var index = SelectedIndex(ImageOpenModeChoice);
            if (index < 0) return;
            AppPreferences.Update(AppPreferences.Current with { ImageOpenMode = (ImageOpenMode)index });
        };
        BindSwitch((FluentToggleSwitch)ImageRestoreSwitch!, "启动时打开上次的图片", value =>
            AppPreferences.Update(AppPreferences.Current with { ImageRestoreOnStartup = value }));
        OpenImageDirectoryButton.Click += (_, _) => OpenLastImageDirectory();
        SetDefaultViewerButton.Click += (_, _) => DefaultImageViewer.OpenSettingsFor(this);
        AutomationProperties.SetName(SetDefaultViewerButton, "去系统里设置默认图片查看器");

        AutomationProperties.SetName(ThemeChoice, "应用主题");
        AutomationProperties.SetName(PenWidth, "画笔粗细");
        ThemeChoice.SelectionChanged += (_, _) =>
        {
            if (_sync) return;
            var index = SelectedIndex(ThemeChoice);
            if (index >= 0) AppPreferences.Update(AppPreferences.Current with { Theme = (AppTheme)index });
        };
        PenWidth.ValueChanged += (_, _) =>
        {
            ((TextBlock)PenWidthValueText!).Text = $"{Math.Round(PenWidth.Value):0} px";
            if (_sync) return;
            // 改的是<b>当前选中的那支笔</b>，不是某个全局值 —— 这正是"两个笔按钮数据独立"：
            // 在红笔上调粗细，不会把蓝笔一起改掉。
            ToolbarTools.UpdateSelectedPen(pen => pen with { Thickness = PenWidth.Value });
        };
        ((Button)ResetInkButton!).Click += (_, _) =>
        {
            // 笔锋的落点是确定的「标准」档：这个按钮叫"重置书写参数"，
            // 不该因为当前是不是自定义而有时生效、有时不生效。
            InkTipOptions.ResetToDefault();
            ToolbarTools.UpdateSelectedPen(pen => pen with { Thickness = 4 });
            AppPreferences.Update(AppPreferences.Current with { Pressure = false });
        };
        WireTipControls();
        WireToolbarControls();
        WireCanvasControls();
    }

    /// <summary>
    /// 「画布」页的接线。<b>逐场景点名</b>：这一页同时摆着两块画布的行，
    /// 所以不存在"当前场景"这个隐式游标（<see cref="CanvasOptions"/> 那边也没有了）。
    /// <para>
    /// 穿透与冻结都是"下次进画布才看得出来"的那种开关，所以除了绑开关本身，
    /// 还<b>用一句话把当前行为念出来</b>（见 <see cref="CanvasBehaviorSummary"/>）——
    /// 拨完开关没有任何即时反馈时，这句话是用户唯一能确认"它记住了"的地方。
    /// </para>
    /// </summary>
    private void WireCanvasControls()
    {
        BindSwitch((FluentToggleSwitch)PassThroughSwitch!, "穿透模式", value =>
            CanvasOptions.SetPassThrough(CanvasScene.ScreenAnnotation, value));
        BindSwitch((FluentToggleSwitch)FreezeSwitch!, "冻结模式", value =>
            CanvasOptions.SetFreeze(CanvasScene.ScreenAnnotation, value));

        BuildBackgroundSwatches();

        CanvasOptions.Changed += SyncCanvasSection;
        SyncCanvasSection();
    }

    /// <summary>
    /// 白板底色那三颗。<b>整块由 <see cref="CanvasBackgroundPalette"/> 生成</b>，
    /// 标记里只有一个空容器 —— 与笔菜单那九格同一个理由：颜色抄进标记就是两张表，
    /// 改色板时必漏一处，而漏了没有任何东西会报错。
    /// <para>样式靠具名点键：隐式样式按精确类型查，别指望 <c>RadioButton</c> 在这里命中
    /// 那套色板格子（库的隐式行是普通单选圈）。</para>
    /// </summary>
    private void BuildBackgroundSwatches()
    {
        var host = (StackPanel)WhiteboardBackgroundSwatches!;
        var colors = CanvasBackgroundPalette.Colors;
        _backgroundRings = new RadioButton[colors.Length];

        for (var i = 0; i < colors.Length; i++)
        {
            var index = i;
            var ring = new RadioButton
            {
                GroupName = "CanvasBackground",
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ring.SetResourceReference(StyleProperty, "PenColorSwatchStyle");
            ring.Background = new SolidColorBrush(colors[index]);
            var name = CanvasBackgroundPalette.Names[index];
            AutomationProperties.SetName(ring, name);
            ring.Checked += (_, _) =>
            {
                if (_sync) return;
                CanvasOptions.SetBackground(CanvasScene.Whiteboard, Argb.Pack(colors[index]));
            };

            _backgroundRings[index] = ring;
            host.Children.Add(ring);
        }
    }

    private RadioButton[] _backgroundRings = [];

    private void SyncCanvasSection(CanvasScene scene) => SyncCanvasSection();

    private void SyncCanvasSection()
    {
        var annotation = CanvasOptions.For(CanvasScene.ScreenAnnotation);
        var whiteboard = CanvasOptions.For(CanvasScene.Whiteboard);

        _sync = true;
        try
        {
            ((FluentToggleSwitch)PassThroughSwitch!).IsChecked = annotation.PassThrough;
            ((FluentToggleSwitch)FreezeSwitch!).IsChecked = annotation.Freeze;

            var selected = CanvasBackgroundPalette.NearestIndex(whiteboard.BackgroundArgb);
            for (var i = 0; i < _backgroundRings.Length; i++)
                _backgroundRings[i].IsChecked = i == selected;
        }
        finally { _sync = false; }

        ((TextBlock)CanvasBehaviorText!).Text = CanvasBehaviorSummary(annotation);

        // 底色与那两个开关不是一类：它<b>当场生效</b>，所以这一句念的是"现在是什么"，
        // 而不是"下次进画布会怎样"。
        ((TextBlock)WhiteboardBehaviorText!).Text =
            $"现在的背景：{CanvasBackgroundPalette.Names[CanvasBackgroundPalette.NearestIndex(whiteboard.BackgroundArgb)]}。"
            + (CanvasSceneState.IsActive(CanvasScene.Whiteboard) ? "白板正在屏上，改一档立刻看得见。" : "");
    }

    /// <summary>把两个开关翻译成人话。<b>不是装饰</b>：这两个开关生效的时机在别处
    /// （一个在鼠标模式、一个在进入画布的瞬间），念一遍是为了不用去猜。</summary>
    private static string CanvasBehaviorSummary(CanvasSceneSettings annotation)
    {
        var mouse = annotation.PassThrough
            ? "鼠标模式下画布留着，但鼠标与触摸穿到下面的窗口上"
            : "鼠标模式下画布收起来";
        var entering = annotation.Freeze
            ? "进入书写 / 擦除时先截一张屏铺在底下"
            : "进入书写 / 擦除时直接写在实时画面上";
        return $"现在的行为：{mouse}；{entering}。";
    }

    /// <summary>
    /// 笔锋那一块的接线。三条来源都收在 <see cref="SyncTipState"/> 一个刷新点上：
    /// 档位下拉、试写区、18 个参数滑杆读的是同一份状态，因此不会出现"档位显示 A、参数是 B"。
    /// </summary>
    private void WireTipControls()
    {
        BindSwitch((FluentToggleSwitch)TipEnabledSwitch!, "启用笔锋", InkTipOptions.SetEnabled);
        AutomationProperties.SetName(TipPreset, "笔锋档位");
        AutomationProperties.SetName((Button)ResetTipPresetButton!, "恢复为所选档位");
        AutomationProperties.SetName((Button)SaveTipPresetButton!, "存为我的笔锋");
        AutomationProperties.SetName((Button)DeleteTipPresetButton!, "删除我的笔锋");
        AutomationProperties.SetName((Button)ClearTipPreviewButton!, "清空试写");

        TipPreset.SelectionChanged += (_, _) =>
        {
            if (_sync) return;
            var id = SelectedTipPresetId();
            if (id is not null) InkTipOptions.SelectPreset(id);
        };
        ((Button)ResetTipPresetButton!).Click += (_, _) => InkTipOptions.ResetToPreset();
        ((Button)SaveTipPresetButton!).Click += (_, _) => InkTipOptions.SaveCustomPreset();
        ((Button)DeleteTipPresetButton!).Click += (_, _) =>
        {
            var id = SelectedTipPresetId();
            if (id is not null) InkTipOptions.DeleteCustomPreset(id);
        };
        ((Button)ClearTipPreviewButton!).Click += (_, _) => _tipPreview.Clear();

        InkTipOptions.Changed += OnTipOptionsChanged;
        InkTipOptions.PresetsChanged += OnTipPresetsChanged;
        SyncTipState();
    }

    private string? SelectedTipPresetId()
    {
        var index = TipPreset.SelectedIndex;
        return index >= 0 && index < _tipPresetIds.Count ? _tipPresetIds[index] : null;
    }

    // ------------------------------------------------------------------ 工具栏按钮

    /// <summary>
    /// <summary>
    /// 「工具栏」设置那一块的接线：<b>一页两段</b> —— 上面工具栏此刻的样子（拖动改顺序），
    /// 下面组件库（按住拖上来加一件）。数据变了、选中项变了、窗口刚建好，三条来源收在一个刷新点上。
    /// <para>
    /// 这里<b>没有标签页</b>。曾经用 <c>FluentTabView</c> 把这两段拆成两个页面，
    /// 看着"各答一个问题"挺整齐，实际把"从下面拖到上面"变成了"先切过去拿、再切过去放" ——
    /// 手势的方向被界面结构抹掉了，而那是这一页唯一要表达的事。
    /// 而且这一页外面已经是设置页的导航，再套一层标签控件就是导航里套导航。
    /// </para>
    /// <para>
    /// 也<b>没有</b>原来那三颗写死的"加一支笔 / 加一把橡皮 / 加一条分隔线"：
    /// 类别与个数混在一处，于是"再加一个"到底是哪一类说不清，而工具栏上可以有八支笔。
    /// 现在加号在组件库每一格上，一格一类。
    /// </para>
    /// </summary>
    private void WireToolbarControls()
    {
        // 顺序有讲究：组件库要把拖动借给那一排，所以那一排先建。
        _layoutStrip = new ToolbarLayoutStrip((Panel)ToolbarLayoutStripHost!);
        _library = new ToolbarToolLibrary((WrapPanel)ToolbarLibraryTiles!, _layoutStrip);

        ToolbarTools.LayoutChanged += OnToolbarToolsChanged;
        ToolbarTools.SelectionChanged += OnToolbarToolsChanged;
        SyncToolbarSection();
    }

    /// <summary>
    /// 工具列表或选中项变了。<b>两个事件走同一条刷新</b>：
    /// 两页都要重建/刷新，而"画笔粗细"那条滑杆读的是<b>当前选中那一支笔</b> ——
    /// 从工具栏那边改粗细时它也得跟着动，否则设置页会显示一个过期的数。
    /// </summary>
    private void OnToolbarToolsChanged()
    {
        SyncToolbarSection();
        Synchronize(AppPreferences.Current);
    }

    private void SyncToolbarSection()
    {
        ToolLayout.Sync();
        ToolLibrary.Sync();

        var drawing = ToolbarTools.Items.Count(static tool => tool.Kind is ToolbarToolKind.Pen or ToolbarToolKind.Eraser);
        var canAdd = ToolbarTools.Items.Count < ToolbarTools.MaxItems;
        var fixedOnes = string.Join(
            "、",
            ToolbarTools.Items
                .Where(static tool => !tool.IsEditable)
                .Select(static tool => ToolbarToolVisuals.DisplayName(tool)));

        ((TextBlock)ToolbarLayoutHintText!).Text = canAdd
            ? $"共 {ToolbarTools.Items.Count} 项，其中 {drawing} 个可画的工具。固定项（{fixedOnes}）可以移动，不能删除。"
            : $"已到上限（{ToolbarTools.MaxItems} 项）。先在下面删掉几项，再从上面那一片拖进来。";
    }

    private static double SelectedPenThickness() =>
        ToolbarTools.Selected is { Kind: ToolbarToolKind.Pen } pen ? pen.Thickness : 4;

    private void OnTipOptionsChanged()
    {
        SyncTipState();
        InkTipOptions.ApplyTo(_tipPreview.TipSettings);
    }

    private void OnTipPresetsChanged() => SyncTipState();

    private void SyncTipState()
    {
        var previousSync = _sync;
        _sync = true;
        try
        {
            var presets = InkTipOptions.Presets;
            var ids = presets.Select(preset => preset.Id).ToList();
            ids.Add(string.Empty); // 末尾的「自定义」

            // 只有列表真的变了才重建项：拖滑杆时这个方法每一步都会被调到，
            // 重建 ComboBox 的项会顺手把选中态和下拉状态一起清掉。
            if (!ids.SequenceEqual(_tipPresetIds))
            {
                _tipPresetIds.Clear();
                _tipPresetIds.AddRange(ids);
                TipPreset.Items.Clear();
                foreach (var preset in presets)
                    TipPreset.Items.Add(new ComboBoxItem { Content = preset.DisplayName });
                TipPreset.Items.Add(new ComboBoxItem { Content = "自定义" });
            }

            // 认不出档位（自定义）就选到最后那一项，而不是硬选一个名字对不上的档。
            var index = _tipPresetIds.IndexOf(InkTipOptions.PresetId);
            if (index < 0) index = _tipPresetIds.Count - 1;
            if (TipPreset.SelectedIndex != index) TipPreset.SelectedIndex = index;

            var selected = InkTipOptions.FindPreset(InkTipOptions.PresetId);
            ((TextBlock)TipPresetDescriptionText!).Text = selected is null
                ? "当前参数不来自任何档位。改滑杆会保持在这个状态，选一个档位即可回到预设。"
                : selected.Description;

            ((FluentToggleSwitch)TipEnabledSwitch!).IsChecked = InkTipOptions.Enabled;
            ((Button)ResetTipPresetButton!).IsEnabled = InkTipOptions.CanReset;
            ((Button)SaveTipPresetButton!).IsEnabled = InkTipOptions.CanSaveCustomPreset;
            ((Button)DeleteTipPresetButton!).IsEnabled = selected is { IsBuiltIn: false };
        }
        finally { _sync = previousSync; }

        _tipEditor.Sync();
    }

    /// <summary>
    /// 方向键走焦点、回车/空格选中 —— 这两条都在 <see cref="FluentNavigationView"/> 里面，
    /// 应用侧不再自己拦一遍。
    /// </summary>
    private void OnNavigationSelectionChanged(object? sender, FluentNavigationSelectionChangedEventArgs e)
    {
        foreach (var (page, item) in _navigation)
            if (ReferenceEquals(item, e.SelectedItem))
            {
                NavigateTo(page);
                return;
            }
    }

    private void ClearVisibleTooltips() => ClearVisibleTooltipsCore(this);

    private static void ClearVisibleTooltipsCore(DependencyObject? root)
    {
        if (root is null) return;
        if (root is FrameworkElement element) element.ToolTip = null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ClearVisibleTooltipsCore(VisualTreeHelper.GetChild(root, i));
    }

    private void BindSwitch(FluentToggleSwitch control, string name, Action<bool> change)
    {
        AutomationProperties.SetName(control, name);
        control.Checked += Handle;
        control.Unchecked += Handle;
        void Handle(object sender, RoutedEventArgs e)
        {
            if (!_sync) change(control.IsChecked == true);
        }
    }

    private void OpenLastImageDirectory()
    {
        var directory = AppPreferences.Current.LastImageDirectory;
        if (directory.Length == 0 || !System.IO.Directory.Exists(directory)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    private void Synchronize(PreferenceSnapshot value)    {
        _sync = true;
        try
        {
            ThemeChoice.SelectedItem = ThemeChoice.Items[(int)value.Theme];
            ((FluentToggleSwitch)ReduceMotionSwitch!).IsChecked = value.ReduceMotion;
            ((FluentToggleSwitch)KeepToolbarOnTopSwitch!).IsChecked = value.KeepToolbarOnTop;
            ((FluentToggleSwitch)PressureSwitch!).IsChecked = value.Pressure;

            // 「文件」这一页：打开方式是一份 ComboBox（下标即枚举值），而不是两个单选 ——
            // 两档的东西做成"二选一"控件会多一份互斥状态要同步，而这里它就是一个枚举。
            ImageOpenModeChoice.SelectedItem = ImageOpenModeChoice.Items[(int)value.ImageOpenMode];
            ((FluentToggleSwitch)ImageRestoreSwitch!).IsChecked = value.ImageRestoreOnStartup;
            var lastDirectory = value.LastImageDirectory;
            ((TextBlock)LastImageDirectoryText!).Text = lastDirectory.Length > 0
                ? lastDirectory
                : "还没打开过图片";
            OpenImageDirectoryButton.IsEnabled = lastDirectory.Length > 0;
            ((TextBlock)DefaultViewerHintText!).Text = DefaultImageViewer.HintText;

            // 粗细这一项现在属于"当前选中的那支笔"：选中的不是笔时滑杆没有对象，直接禁用 ——
            // 留一条能动但改了没反应的滑杆比禁用更糟。
            var thickness = SelectedPenThickness();
            var isPen = ToolbarTools.Selected is { Kind: ToolbarToolKind.Pen };
            PenWidth.Value = thickness;
            PenWidth.IsEnabled = isPen;
            ((TextBlock)PenWidthValueText!).Text = isPen ? $"{thickness:0} px" : "未选中笔";
        }
        finally { _sync = false; }

        // 试写区跟着画笔粗细走：它要说的是"这一档写出来什么样"，粗细对不上会让人误判笔锋。
        _tipPreview.InkAttributes.Width = SelectedPenThickness();
        _tipPreview.InkAttributes.Height = SelectedPenThickness();
        SyncTipState();
    }

    private static int SelectedIndex(ComboBox combo)
    {
        for (var i = 0; i < combo.Items.Count; i++)
            if (ReferenceEquals(combo.Items[i], combo.SelectedItem)) return i;
        return -1;
    }

    private void UpdateSaveStatus() => ((TextBlock)SaveStatusText!).Text =
        AppPreferences.SaveError ?? (AppPreferences.IsSavePending ? "正在保存更改…" : "更改会自动应用并保存。");

    private void OnThemeChanged()
    {
        if (TitleBar is { } titleBar)
        {
            // 标题栏取的是同一批画笔对象：Astra 换深浅时改的是实例的颜色，不换实例，
            // 所以这里赋一次就长期跟着走，不需要每次主题变化再赋一遍。
            titleBar.Background = FluentThemeManager.GetBrush("SolidBackgroundFillColorBaseBrush");
            titleBar.Foreground = FluentThemeManager.GetBrush("TextFillColorPrimaryBrush");
        }

        // 试写区的底色是主题表面，墨色必须跟着反相，否则深色主题下写出来是"黑底黑字"。
        // 已经写上去的那几笔不动 —— 它们的颜色在落笔时就烘进点数据了，擦掉重写才有新色。
        _tipPreview.InkAttributes.Color = FluentThemeManager.IsDark
            ? new Dusk.Ink.Primitives.InkColor(0xE8, 0xE8, 0xE8, 0xFF)
            : new Dusk.Ink.Primitives.InkColor(0x1A, 0x1A, 0x1A, 0xFF);
    }

    private void NavigateTo(SettingsNavPage page, bool force = false)
    {
        if (!force && page == _page) return;
        ThemeChoice.IsDropDownOpen = false;
        _page = page;
        NavigationRoot!.SelectedItem = _navigation[page];
        PageHost.Children.Clear();
        var panel = _pages[page];
        PageHost.Children.Add(panel);
        ClearVisibleTooltips();
        ((ScrollViewer)SettingsScrollViewer!).ScrollToVerticalOffset(0);
        if (_loaded) FluentThemeManager.Enter(panel);
    }
}
