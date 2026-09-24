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
    private readonly JaliumInkCanvas _tipPreview;

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
        _pages = new()
        {
            [SettingsNavPage.Appearance] = AppearanceSectionPanel!,
            [SettingsNavPage.Ink] = InkSectionPanel!,
            [SettingsNavPage.Interaction] = InteractionSectionPanel!,
            [SettingsNavPage.About] = AboutSectionPanel!,
        };
        _navigation = new()
        {
            [SettingsNavPage.Appearance] = (FluentNavigationItem)AppearanceNavButton!,
            [SettingsNavPage.Ink] = (FluentNavigationItem)InkNavButton!,
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

            // 墨迹控件必须显式拆：Jalium 不代调，而它挂着整棵墨迹视觉树（见 AGENTS）。
            _tipPreview.Dispose();
            AppPreferences.Flush();
        };
    }

    private void WireControls()
    {
        foreach (var (page, item) in _navigation)
            AutomationProperties.SetName(item, (string?)item.Content ?? page.ToString());
        NavigationRoot!.SelectionChanged += OnNavigationSelectionChanged;
        BindSwitch((FluentToggleSwitch)ReduceMotionSwitch!, "减少动画", value =>
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = value }));
        BindSwitch((FluentToggleSwitch)KeepToolbarOnTopSwitch!, "始终置顶工具栏", value =>
            AppPreferences.Update(AppPreferences.Current with { KeepToolbarOnTop = value }));
        BindSwitch((FluentToggleSwitch)PressureSwitch!, "压力感应", InkRuntimeOptions.SetEnablePressure);
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
            if (!_sync) AppPreferences.Update(AppPreferences.Current with { PenWidth = PenWidth.Value });
        };
        ((Button)ResetInkButton!).Click += (_, _) =>
        {
            // 笔锋的落点是确定的「标准」档：这个按钮叫"重置书写参数"，
            // 不该因为当前是不是自定义而有时生效、有时不生效。
            InkTipOptions.ResetToDefault();
            AppPreferences.Update(AppPreferences.Current with { PenWidth = 4, Pressure = false });
        };
        WireTipControls();
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

    private void Synchronize(PreferenceSnapshot value)
    {
        _sync = true;
        try
        {
            ThemeChoice.SelectedItem = ThemeChoice.Items[(int)value.Theme];
            ((FluentToggleSwitch)ReduceMotionSwitch!).IsChecked = value.ReduceMotion;
            ((FluentToggleSwitch)KeepToolbarOnTopSwitch!).IsChecked = value.KeepToolbarOnTop;
            ((FluentToggleSwitch)PressureSwitch!).IsChecked = value.Pressure;
            PenWidth.Value = value.PenWidth;
            ((TextBlock)PenWidthValueText!).Text = $"{value.PenWidth:0} px";
        }
        finally { _sync = false; }

        // 试写区跟着画笔粗细走：它要说的是"这一档写出来什么样"，粗细对不上会让人误判笔锋。
        _tipPreview.InkAttributes.Width = value.PenWidth;
        _tipPreview.InkAttributes.Height = value.PenWidth;
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
        ((ScrollViewer)SettingsScrollViewer!).ScrollToVerticalOffset(0);
        if (_loaded) FluentThemeManager.Enter(panel);
    }
}
