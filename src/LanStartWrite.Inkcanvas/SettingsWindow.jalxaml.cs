using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class SettingsWindow : Window
{
    private const double SwitchThumbOnMarginLeft = 22;

    private static readonly SolidColorBrush SectionIconBrush = new(Color.FromRgb(0x24, 0x24, 0x24));
    private static readonly Brush NavSelectionBrush =
        new SolidColorBrush(Color.FromArgb(38, 0, 120, 212));
    private static readonly Brush NavIdleBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private static readonly Brush WindowChromeBrush =
        new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

    private const double ExpandedPaneWidth = 236;
    private const double CompactPaneWidth = 64;

    private Button AccentContrastSwitchButtonEl =>
        (Button)AccentContrastSwitchButton!;
    private Border AccentContrastSwitchTrackEl =>
        (Border)AccentContrastSwitchTrack!;
    private Border AccentContrastSwitchThumbEl =>
        (Border)AccentContrastSwitchThumb!;

    private Button FollowSystemThemeSwitchButtonEl =>
        (Button)FollowSystemThemeSwitchButton!;
    private Border FollowSystemThemeSwitchTrackEl =>
        (Border)FollowSystemThemeSwitchTrack!;
    private Border FollowSystemThemeSwitchThumbEl =>
        (Border)FollowSystemThemeSwitchThumb!;

    private Button KeepToolbarOnTopSwitchButtonEl =>
        (Button)KeepToolbarOnTopSwitchButton!;
    private Border KeepToolbarOnTopSwitchTrackEl =>
        (Border)KeepToolbarOnTopSwitchTrack!;
    private Border KeepToolbarOnTopSwitchThumbEl =>
        (Border)KeepToolbarOnTopSwitchThumb!;
    private Button RealtimeSamplingSwitchButtonEl =>
        (Button)RealtimeSamplingSwitchButton!;
    private Border RealtimeSamplingSwitchTrackEl =>
        (Border)RealtimeSamplingSwitchTrack!;
    private Border RealtimeSamplingSwitchThumbEl =>
        (Border)RealtimeSamplingSwitchThumb!;
    private Button PressureSwitchButtonEl =>
        (Button)PressureSwitchButton!;
    private Border PressureSwitchTrackEl =>
        (Border)PressureSwitchTrack!;
    private Border PressureSwitchThumbEl =>
        (Border)PressureSwitchThumb!;
    private Button TiltSwitchButtonEl =>
        (Button)TiltSwitchButton!;
    private Border TiltSwitchTrackEl =>
        (Border)TiltSwitchTrack!;
    private Border TiltSwitchThumbEl =>
        (Border)TiltSwitchThumb!;

    private bool _accentContrast;
    private bool _followSystemTheme;
    private bool _keepToolbarOnTop = true;
    private bool _realtimeSampling = InkRuntimeOptions.Current.EnableRealtimeSampling;
    private bool _pressureMapping = InkRuntimeOptions.Current.EnablePressure;
    private bool _tiltMapping = InkRuntimeOptions.Current.EnableTilt;

    private Border NavigationPane => (Border)NavigationPaneRoot!;

    private Button HamburgerBtn => (Button)HamburgerButton!;

    private Button AppearanceBtn => (Button)AppearanceNavButton!;
    private Border AppearanceSelBorder => (Border)AppearanceNavSelectionBorder!;
    private StackPanel AppearanceRow => (StackPanel)AppearanceNavRow!;
    private TextBlock AppearanceLbl => (TextBlock)AppearanceNavLabel!;

    private Button InkBtn => (Button)InkNavButton!;
    private Border InkSelBorder => (Border)InkNavSelectionBorder!;
    private StackPanel InkRow => (StackPanel)InkNavRow!;
    private TextBlock InkLbl => (TextBlock)InkNavLabel!;

    private Button InteractionBtn => (Button)InteractionNavButton!;
    private Border InteractionSelBorder => (Border)InteractionNavSelectionBorder!;
    private StackPanel InteractionRow => (StackPanel)InteractionNavRow!;
    private TextBlock InteractionLbl => (TextBlock)InteractionNavLabel!;

    private Button AboutBtn => (Button)AboutNavButton!;
    private Border AboutSelBorder => (Border)AboutNavSelectionBorder!;
    private StackPanel AboutRow => (StackPanel)AboutNavRow!;
    private TextBlock AboutLbl => (TextBlock)AboutNavLabel!;

    private Slider PenWidthSliderEl => (Slider)PenWidthSlider!;
    private Slider MinPointDistanceSliderEl => (Slider)MinPointDistanceSlider!;
    private TextBlock PenWidthValueTextEl => (TextBlock)PenWidthValueText!;
    private TextBlock MinPointDistanceValueTextEl => (TextBlock)MinPointDistanceValueText!;
    private ComboBox SmoothingLevelComboBoxEl => (ComboBox)SmoothingLevelComboBox!;
    private TextBlock AboutVersionTextEl => (TextBlock)AboutVersionText!;

    private FrameworkElement AppearancePanel => (FrameworkElement)AppearanceSectionPanel!;
    private FrameworkElement InkPanel => (FrameworkElement)InkSectionPanel!;
    private FrameworkElement InteractionPanel => (FrameworkElement)InteractionSectionPanel!;
    private FrameworkElement AboutPanel => (FrameworkElement)AboutSectionPanel!;

    private SettingsNavPage _navPage;

    /// <summary>左侧导航窄带模式（仅图标），对应 WinUI NavigationView Compact 的常见形态。</summary>
    private bool _isPaneCompact;

    public SettingsWindow()
    {
        InitializeComponent();

        AllowsTransparency = false;
        SystemBackdrop = WindowBackdropType.None;
        Background = WindowChromeBrush;

        ApplySwitchVisuals();
        NavigateTo(SettingsNavPage.Appearance, force: true);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyStrokePreviewGlyph();
        WirePenSlider();
        WireInkRuntimeControls();
        ApplyVersionText();
    }

    private void ApplyStrokePreviewGlyph()
    {
        if (StrokePreviewIcon is SymbolIcon si)
            si.Foreground = SectionIconBrush;
    }

    private void WirePenSlider()
    {
        var s = PenWidthSliderEl;
        s.ValueChanged += (_, _) => UpdatePenWidthLabel();
        UpdatePenWidthLabel();
    }

    private void WireInkRuntimeControls()
    {
        var runtime = InkRuntimeOptions.Current;
        _realtimeSampling = runtime.EnableRealtimeSampling;
        _pressureMapping = runtime.EnablePressure;
        _tiltMapping = runtime.EnableTilt;

        MinPointDistanceSliderEl.Value = runtime.MinPointDistance;
        MinPointDistanceSliderEl.ValueChanged += (_, _) =>
        {
            InkRuntimeOptions.SetMinPointDistance(MinPointDistanceSliderEl.Value);
            UpdateMinPointDistanceLabel();
        };
        UpdateMinPointDistanceLabel();

        SmoothingLevelComboBoxEl.SelectedIndex = runtime.SmoothingLevel switch
        {
            InkSmoothingLevel.Low => 0,
            InkSmoothingLevel.High => 2,
            _ => 1,
        };
        SmoothingLevelComboBoxEl.SelectionChanged += (_, _) =>
        {
            var level = SmoothingLevelComboBoxEl.SelectedIndex switch
            {
                0 => InkSmoothingLevel.Low,
                2 => InkSmoothingLevel.High,
                _ => InkSmoothingLevel.Balanced,
            };
            InkRuntimeOptions.SetSmoothingLevel(level);
        };
    }

    private void UpdatePenWidthLabel()
    {
        var v = (int)Math.Round(PenWidthSliderEl.Value);
        PenWidthValueTextEl.Text = $"{v} px";
    }

    private void UpdateMinPointDistanceLabel()
    {
        MinPointDistanceValueTextEl.Text = $"{MinPointDistanceSliderEl.Value:F2} px";
    }

    private void ApplyVersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null)
            AboutVersionTextEl.Text = $"版本 {v.Major}.{v.Minor}.{v.Build}";
    }

    private void ApplySwitchVisuals()
    {
        // AccentContrast
        AccentContrastSwitchTrackEl.Background = _accentContrast
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        AccentContrastSwitchThumbEl.HorizontalAlignment = HorizontalAlignment.Left;
        AccentContrastSwitchThumbEl.Margin = new Thickness(
            _accentContrast ? SwitchThumbOnMarginLeft : 0, 0, 0, 0);

        // FollowSystemTheme (currently reserved/disabled in UI)
        FollowSystemThemeSwitchTrackEl.Background = _followSystemTheme
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        FollowSystemThemeSwitchThumbEl.HorizontalAlignment = HorizontalAlignment.Left;
        FollowSystemThemeSwitchThumbEl.Margin = new Thickness(
            _followSystemTheme ? SwitchThumbOnMarginLeft : 0, 0, 0, 0);

        // KeepToolbarOnTop
        KeepToolbarOnTopSwitchTrackEl.Background = _keepToolbarOnTop
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        KeepToolbarOnTopSwitchThumbEl.HorizontalAlignment = HorizontalAlignment.Left;
        KeepToolbarOnTopSwitchThumbEl.Margin = new Thickness(
            _keepToolbarOnTop ? SwitchThumbOnMarginLeft : 0, 0, 0, 0);

        // RealtimeSampling
        RealtimeSamplingSwitchTrackEl.Background = _realtimeSampling
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        RealtimeSamplingSwitchThumbEl.HorizontalAlignment = HorizontalAlignment.Left;
        RealtimeSamplingSwitchThumbEl.Margin = new Thickness(
            _realtimeSampling ? SwitchThumbOnMarginLeft : 0, 0, 0, 0);

        // PressureMapping
        PressureSwitchTrackEl.Background = _pressureMapping
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        PressureSwitchThumbEl.HorizontalAlignment = HorizontalAlignment.Left;
        PressureSwitchThumbEl.Margin = new Thickness(
            _pressureMapping ? SwitchThumbOnMarginLeft : 0, 0, 0, 0);

        // Tilt mapping
        TiltSwitchTrackEl.Background = _tiltMapping
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        TiltSwitchThumbEl.HorizontalAlignment = HorizontalAlignment.Left;
        TiltSwitchThumbEl.Margin = new Thickness(
            _tiltMapping ? SwitchThumbOnMarginLeft : 0, 0, 0, 0);
    }

    private void AccentContrastSwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _accentContrast = !_accentContrast;
        ApplySwitchVisuals();
    }

    private void FollowSystemThemeSwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _followSystemTheme = !_followSystemTheme;
        ApplySwitchVisuals();
    }

    private void KeepToolbarOnTopSwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _keepToolbarOnTop = !_keepToolbarOnTop;
        ApplySwitchVisuals();
    }

    private void RealtimeSamplingSwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _realtimeSampling = !_realtimeSampling;
        InkRuntimeOptions.SetRealtimeSampling(_realtimeSampling);
        ApplySwitchVisuals();
    }

    private void PressureSwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _pressureMapping = !_pressureMapping;
        InkRuntimeOptions.SetEnablePressure(_pressureMapping);
        ApplySwitchVisuals();
    }

    private void TiltSwitchButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _tiltMapping = !_tiltMapping;
        InkRuntimeOptions.SetEnableTilt(_tiltMapping);
        ApplySwitchVisuals();
    }

    private void HamburgerButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _isPaneCompact = !_isPaneCompact;
        ApplyPaneChrome();
    }

    private void AppearanceNav_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateTo(SettingsNavPage.Appearance);
    }

    private void InkNav_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateTo(SettingsNavPage.Ink);
    }

    private void InteractionNav_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateTo(SettingsNavPage.Interaction);
    }

    private void AboutNav_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateTo(SettingsNavPage.About);
    }

    private void NavigateTo(SettingsNavPage page, bool force = false)
    {
        if (!force && page == _navPage)
            return;
        _navPage = page;

        var appearance = page == SettingsNavPage.Appearance;
        AppearancePanel.Visibility = appearance ? Visibility.Visible : Visibility.Collapsed;

        var ink = page == SettingsNavPage.Ink;
        InkPanel.Visibility = ink ? Visibility.Visible : Visibility.Collapsed;

        var interaction = page == SettingsNavPage.Interaction;
        InteractionPanel.Visibility = interaction ? Visibility.Visible : Visibility.Collapsed;

        var about = page == SettingsNavPage.About;
        AboutPanel.Visibility = about ? Visibility.Visible : Visibility.Collapsed;

        AppearanceSelBorder.Background = appearance ? NavSelectionBrush : NavIdleBrush;
        InkSelBorder.Background = ink ? NavSelectionBrush : NavIdleBrush;
        InteractionSelBorder.Background = interaction ? NavSelectionBrush : NavIdleBrush;
        AboutSelBorder.Background = about ? NavSelectionBrush : NavIdleBrush;
    }

    private void ApplyPaneChrome()
    {
        NavigationPane.Width = _isPaneCompact ? CompactPaneWidth : ExpandedPaneWidth;

        var labelsVisible = !_isPaneCompact;
        AppearanceLbl.Visibility = labelsVisible ? Visibility.Visible : Visibility.Collapsed;
        InkLbl.Visibility = labelsVisible ? Visibility.Visible : Visibility.Collapsed;
        InteractionLbl.Visibility = labelsVisible ? Visibility.Visible : Visibility.Collapsed;
        AboutLbl.Visibility = labelsVisible ? Visibility.Visible : Visibility.Collapsed;

        var rowAlign = _isPaneCompact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        AppearanceRow.HorizontalAlignment = rowAlign;
        InkRow.HorizontalAlignment = rowAlign;
        InteractionRow.HorizontalAlignment = rowAlign;
        AboutRow.HorizontalAlignment = rowAlign;

        HamburgerBtn.HorizontalAlignment =
            _isPaneCompact ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        void StyleNavBtn(Button b)
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (_isPaneCompact)
            {
                b.Padding = new Thickness(10);
                b.Margin = new Thickness(4);
            }
            else
            {
                b.Padding = new Thickness(14, 10, 14, 10);
                b.Margin = new Thickness(4);
            }
        }

        StyleNavBtn(AppearanceBtn);
        StyleNavBtn(InkBtn);
        StyleNavBtn(InteractionBtn);
        StyleNavBtn(AboutBtn);
    }
}

