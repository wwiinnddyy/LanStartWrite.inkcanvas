using System.Reflection;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class SettingsWindow : Window
{
    private readonly Dictionary<SettingsNavPage, FrameworkElement> _pages;
    private readonly Dictionary<SettingsNavPage, (FluentNavigationItem Button, TextBlock Label)> _navigation;
    private readonly NavigationIndicatorAnimator _navigationIndicator;
    private bool _sync;
    private bool _loaded;
    private bool? _manualCompact;
    private bool _compact;
    private bool _lastNarrow;
    private double _layoutWidth = 960;
    private SettingsNavPage _page;

    private Grid PageHost => (Grid)SettingsContentHost!;
    private Border Pane => (Border)NavigationPaneRoot!;
    private Slider PenWidth => (Slider)PenWidthSlider!;
    private Slider PointDistance => (Slider)MinPointDistanceSlider!;
    private ComboBox ThemeChoice => (ComboBox)ThemeComboBox!;
    private ComboBox SmoothingChoice => (ComboBox)SmoothingLevelComboBox!;

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
            [SettingsNavPage.Appearance] = ((FluentNavigationItem)AppearanceNavButton!, (TextBlock)AppearanceNavLabel!),
            [SettingsNavPage.Ink] = ((FluentNavigationItem)InkNavButton!, (TextBlock)InkNavLabel!),
            [SettingsNavPage.Interaction] = ((FluentNavigationItem)InteractionNavButton!, (TextBlock)InteractionNavLabel!),
            [SettingsNavPage.About] = ((FluentNavigationItem)AboutNavButton!, (TextBlock)AboutNavLabel!),
        };
        _navigationIndicator = new NavigationIndicatorAnimator((Border)NavigationSelectionIndicator!);
        NavigationPaneLayout!.LayoutUpdated += NavigationPane_OnLayoutUpdated;
        // Only the current page belongs to the live tree: no hidden controls in Tab/UIA.
        PageHost.Children.Clear();
        NavigateTo(SettingsNavPage.Appearance, force: true);

        WireControls();
        Synchronize(AppPreferences.Current);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        ((TextBlock)AboutVersionText!).Text = $"版本 {version?.Major}.{version?.Minor}.{version?.Build} · Jalium.UI 26.10.9";
        UpdateSaveStatus();
        UpdatePane();

        AppPreferences.Changed += Synchronize;
        AppPreferences.SaveStatusChanged += UpdateSaveStatus;
        FluentTheme.Changed += OnThemeChanged;
        SystemSettingsChanged += (_, _) => FluentTheme.ApplyPreferences();
        Loaded += (_, _) =>
        {
            _loaded = true;
            if (Content is FrameworkElement layout && layout.RenderSize.Width > 0)
                _layoutWidth = layout.RenderSize.Width;
            UpdatePane();
            OnThemeChanged();
            FluentTheme.ApplyMotionPolicy(this);
        };
        // The content root, unlike the Window's declared Width, follows native client resizing.
        ((FrameworkElement)Content!).SizeChanged += (_, e) =>
        {
            _layoutWidth = e.NewSize.Width;
            var narrow = _layoutWidth < 800;
            if (narrow != _lastNarrow) _manualCompact = null;
            _lastNarrow = narrow;
            UpdatePane();
        };
        Closed += (_, _) =>
        {
            NavigationPaneLayout!.LayoutUpdated -= NavigationPane_OnLayoutUpdated;
            _navigationIndicator.Complete();
            AppPreferences.Changed -= Synchronize;
            AppPreferences.SaveStatusChanged -= UpdateSaveStatus;
            FluentTheme.Changed -= OnThemeChanged;
            AppPreferences.Flush();
        };
    }

    private void WireControls()
    {
        foreach (var item in _navigation.Values)
        {
            AutomationProperties.SetName(item.Button, item.Label.Text);
            item.Button.PreviewKeyDown += Navigation_OnPreviewKeyDown;
        }
        AutomationProperties.SetName((Button)HamburgerButton!, "展开或折叠导航");
        BindSwitch((FluentToggleSwitch)ReduceMotionSwitch!, "减少动画", value =>
            AppPreferences.Update(AppPreferences.Current with { ReduceMotion = value }));
        BindSwitch((FluentToggleSwitch)KeepToolbarOnTopSwitch!, "始终置顶工具栏", value =>
            AppPreferences.Update(AppPreferences.Current with { KeepToolbarOnTop = value }));
        BindSwitch((FluentToggleSwitch)PressureSwitch!, "压力感应", InkRuntimeOptions.SetEnablePressure);
        BindSwitch((FluentToggleSwitch)RealtimeSamplingSwitch!, "实时采样通道", InkRuntimeOptions.SetRealtimeSampling);
        BindSwitch((FluentToggleSwitch)TiltSwitch!, "倾斜数据采集", InkRuntimeOptions.SetEnableTilt);
        AutomationProperties.SetName(ThemeChoice, "应用主题");
        AutomationProperties.SetName(SmoothingChoice, "平滑等级");
        AutomationProperties.SetName(PenWidth, "画笔粗细");
        AutomationProperties.SetName(PointDistance, "最小采样点距");
        ThemeChoice.SelectionChanged += (_, _) =>
        {
            if (_sync) return;
            var index = SelectedIndex(ThemeChoice);
            if (index >= 0) AppPreferences.Update(AppPreferences.Current with { Theme = (AppTheme)index });
        };
        SmoothingChoice.SelectionChanged += (_, _) =>
        {
            if (_sync) return;
            var index = SelectedIndex(SmoothingChoice);
            if (index >= 0) InkRuntimeOptions.SetSmoothingLevel((InkSmoothingLevel)index);
        };
        PenWidth.ValueChanged += (_, _) =>
        {
            ((TextBlock)PenWidthValueText!).Text = $"{Math.Round(PenWidth.Value):0} px";
            if (!_sync) AppPreferences.Update(AppPreferences.Current with { PenWidth = PenWidth.Value });
        };
        PointDistance.ValueChanged += (_, _) =>
        {
            ((TextBlock)MinPointDistanceValueText!).Text = $"{PointDistance.Value:F2} px";
            if (!_sync) InkRuntimeOptions.SetMinPointDistance(PointDistance.Value);
        };
        ((Button)ResetInkButton!).Click += (_, _) =>
        {
            AppPreferences.Update(AppPreferences.Current with
            {
                PenWidth = 4, Pressure = false, Tilt = false, RealtimeSampling = true,
                Smoothing = InkSmoothingLevel.Balanced, MinPointDistance = 0.75,
            });
        };
    }

    private void Navigation_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardModifiers != ModifierKeys.None) return;
        var buttons = _navigation.Values.Select(item => item.Button).ToArray();
        var current = Array.FindIndex(buttons, button => ReferenceEquals(button, sender));
        if (current < 0) return;
        var target = e.Key switch
        {
            Key.Down => (current + 1) % buttons.Length,
            Key.Up => (current + buttons.Length - 1) % buttons.Length,
            Key.Home => 0,
            Key.End => buttons.Length - 1,
            _ => -1,
        };
        if (target < 0) return;
        buttons[target].Focus();
        e.Handled = true;
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
            SmoothingChoice.SelectedItem = SmoothingChoice.Items[(int)value.Smoothing];
            ((FluentToggleSwitch)ReduceMotionSwitch!).IsChecked = value.ReduceMotion;
            ((FluentToggleSwitch)KeepToolbarOnTopSwitch!).IsChecked = value.KeepToolbarOnTop;
            ((FluentToggleSwitch)PressureSwitch!).IsChecked = value.Pressure;
            ((FluentToggleSwitch)RealtimeSamplingSwitch!).IsChecked = value.RealtimeSampling;
            ((FluentToggleSwitch)TiltSwitch!).IsChecked = value.Tilt;
            PenWidth.Value = value.PenWidth;
            PointDistance.Value = value.MinPointDistance;
            ((TextBlock)PenWidthValueText!).Text = $"{value.PenWidth:0} px";
            ((TextBlock)MinPointDistanceValueText!).Text = $"{value.MinPointDistance:F2} px";
        }
        finally { _sync = false; }
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
        Background = FluentTheme.Brush("SolidBackgroundFillColorBaseBrush");
        Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush");
        if (TitleBar is { } titleBar)
        {
            titleBar.Background = Background;
            titleBar.Foreground = Foreground;
        }
        UpdateNavigation();
        if (!FluentTheme.AnimationsEnabled) _navigationIndicator.Complete();
    }

    private void NavigateTo(SettingsNavPage page, bool force = false)
    {
        if (!force && page == _page) return;
        ThemeChoice.IsDropDownOpen = false;
        SmoothingChoice.IsDropDownOpen = false;
        _page = page;
        UpdateNavigation(animateIndicator: _loaded);
        PageHost.Children.Clear();
        var panel = _pages[page];
        PageHost.Children.Add(panel);
        ((ScrollViewer)SettingsScrollViewer!).ScrollToVerticalOffset(0);
        if (_loaded)
        {
            FluentTheme.ApplyMotionPolicy(panel);
            FluentTheme.Enter(panel);
        }
    }

    private void UpdateNavigation(bool animateIndicator = false)
    {
        foreach (var (page, parts) in _navigation)
            parts.Button.IsSelected = page == _page;
        UpdateSelectionIndicator(animateIndicator);
    }

    private void NavigationPane_OnLayoutUpdated(object? sender, EventArgs e) => UpdateSelectionIndicator(false);

    private void UpdateSelectionIndicator(bool animate)
    {
        var item = _navigation[_page].Button;
        var layer = (Canvas)NavigationIndicatorLayer!;
        if (item.ActualHeight <= 0 || layer.ActualHeight <= 0) return;
        var transform = item.TransformToVisual(layer);
        if (transform is null) return;
        var position = transform.Transform(new Point(0,
            (item.ActualHeight - NavigationIndicatorAnimator.RestingHeight) / 2));
        // Use laid-out item positions: compact mode, DPI changes and the footer item
        // must not depend on a hard-coded row index or cached window height.
        _navigationIndicator.MoveTo(position.X, position.Y,
            animate && _loaded && FluentTheme.AnimationsEnabled);
    }

    private void UpdatePane()
    {
        _compact = _manualCompact ?? (_layoutWidth < 800);
        Pane.Width = _compact ? 48 : 220;
        PageHost.Margin = new Thickness(_layoutWidth < 640 ? 16 : 24);
        foreach (var parts in _navigation.Values)
            parts.Label.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HamburgerButton_OnClick(object sender, RoutedEventArgs e) { _manualCompact = !_compact; UpdatePane(); }
    private void AppearanceNav_OnClick(object sender, RoutedEventArgs e) => NavigateTo(SettingsNavPage.Appearance);
    private void InkNav_OnClick(object sender, RoutedEventArgs e) => NavigateTo(SettingsNavPage.Ink);
    private void InteractionNav_OnClick(object sender, RoutedEventArgs e) => NavigateTo(SettingsNavPage.Interaction);
    private void AboutNav_OnClick(object sender, RoutedEventArgs e) => NavigateTo(SettingsNavPage.About);
}
