using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

public partial class EraserSecondaryMenuWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    // 二次点击确认的窗口期：过了就退回"清空全部"，免得按钮悄悄停在待确认状态上。
    private static readonly TimeSpan ClearArmWindow = TimeSpan.FromSeconds(3);

    private const string ClearIdleLabel = "清空全部";
    private const string ClearArmedLabel = "再点一次：清空全部";

    private readonly DispatcherTimer _clearArmTimer;
    private bool _stateSync;
    private bool _armedToClear;
    private EraserMode _selectedMode = EraserMode.Area;

    private Slider EraserRadius => (Slider)EraserRadiusSlider!;
    private Button EraseAll => (Button)EraseAllButton!;

    public double SelectedRadius { get; private set; } = 14;

    public event Action<EraserMode>? EraserModeChanged;
    public event Action<double>? EraserRadiusChanged;
    public event Action? ClearRequested;
    public event Action? DismissRequested;

    public EraserSecondaryMenuWindow()
    {
        AllowsTransparency = true;
        ShowActivated = false;
        InitializeComponent();
        SystemBackdrop = WindowBackdropType.None;
        Background = TransparentBrush;
        Opacity = 1;

        EraserAreaRadio!.Checked += OnModeRadioChecked;
        EraserStrokeRadio!.Checked += OnModeRadioChecked;
        EraserRadius.ValueChanged += OnRadiusChanged;
        AutomationProperties.SetName(EraserRadius, "擦除半径");
        AutomationProperties.SetName(EraseAll, ClearIdleLabel);
        EraseAll.Click += OnEraseAllClicked;

        _clearArmTimer = new DispatcherTimer { Interval = ClearArmWindow };
        _clearArmTimer.Tick += (_, _) => DisarmClear();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            DismissRequested?.Invoke();
            e.Handled = true;
        };

        FitSizeToContent();
    }

    public EraserMode SelectedMode => _selectedMode;

    public void SetCurrentState(EraserMode mode, double radius)
    {
        _stateSync = true;
        try
        {
            _selectedMode = mode;
            SelectedRadius = double.IsFinite(radius) ? Math.Clamp(Math.Round(radius), 4, 48) : 14;
            EraserRadius.Value = SelectedRadius;
            EraserAreaRadio!.IsChecked = mode == EraserMode.Area;
            EraserStrokeRadio!.IsChecked = mode == EraserMode.Stroke;
            ApplyModePresentation();
            UpdateRadiusText();
        }
        finally
        {
            _stateSync = false;
        }
    }

    /// <summary>菜单收起时复位确认态：下次打开应当从"清空全部"重新开始，而不是停在待确认。</summary>
    internal void ResetClearConfirmation()
    {
        _clearArmTimer.Stop();
        DisarmClear();
    }

    internal void FocusSelectedMode() => EraserAreaRadio!.Focus();

    private void OnModeRadioChecked(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (_stateSync) return;
        if (sender is not RadioButton radio || radio.IsChecked != true) return;

        var next = ReferenceEquals(radio, EraserStrokeRadio) ? EraserMode.Stroke : EraserMode.Area;
        if (next == _selectedMode) return;

        _selectedMode = next;
        ApplyModePresentation();
        EraserModeChanged?.Invoke(next);
    }

    private void OnRadiusChanged(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_stateSync) return;

        SelectedRadius = Math.Round(EraserRadius.Value);
        UpdateRadiusText();
        EraserRadiusChanged?.Invoke(SelectedRadius);
    }

    /// <summary>
    /// 半径那一行只在面积擦下有意义：面积擦的半径决定"擦掉多宽"，
    /// 而笔迹擦是整笔摘除，命中余量在内核里是另一个固定档位，不给它一个骗人的滑块。
    /// </summary>
    private void ApplyModePresentation()
    {
        var area = _selectedMode == EraserMode.Area;
        var radiusVisibility = area ? Visibility.Visible : Visibility.Collapsed;

        EraserRadius!.IsEnabled = area;
        EraserRadius.Visibility = radiusVisibility;
        ((TextBlock)EraserRadiusLabel!).Visibility = radiusVisibility;
        ((TextBlock)EraserRadiusValueText!).Visibility = radiusVisibility;
        ((TextBlock)EraserHintText!).Text = area
            ? "画过的地方被擦掉，一笔可能被切成几段。"
            : "点中一笔就整笔去掉，不切割、不留碎片。";
    }

    private void UpdateRadiusText()
    {
        ((TextBlock)EraserRadiusValueText!).Text = $"{(int)SelectedRadius} px";
    }

    /// <summary>清空是不可逆动作（批注板没有回收站），所以要点两次。</summary>
    private void OnEraseAllClicked(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (!_armedToClear)
        {
            _armedToClear = true;
            EraseAll.Content = ClearArmedLabel;
            AutomationProperties.SetName(EraseAll, ClearArmedLabel);
            _clearArmTimer.Stop();
            _clearArmTimer.Start();
            return;
        }

        DisarmClear();
        ClearRequested?.Invoke();
    }

    private void DisarmClear()
    {
        _clearArmTimer.Stop();
        if (!_armedToClear) return;

        _armedToClear = false;
        EraseAll.Content = ClearIdleLabel;
        AutomationProperties.SetName(EraseAll, ClearIdleLabel);
    }

    /// <summary>
    /// 26.10.x 的 <c>Window.SizeToContent</c> 还没接线，与笔二级菜单同一处理：
    /// Show 之前手动按内容的 DesiredSize 定窗口尺寸。
    /// </summary>
    private void FitSizeToContent()
    {
        if (Content is not FrameworkElement root)
            return;

        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SizeToContent = SizeToContent.Manual;
        var desired = root.DesiredSize;
        Width = Math.Max(1, Math.Ceiling(desired.Width));
        Height = Math.Max(1, Math.Ceiling(desired.Height));
    }
}
