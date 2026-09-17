using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class PenSecondaryMenuWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private static readonly Color[] PaletteColors =
    [
        Color.FromRgb(0x20, 0x20, 0x20), // 1
        Color.FromRgb(0xD1, 0x34, 0x38), // 2
        Color.FromRgb(0xF2, 0x6B, 0x1F), // 3
        Color.FromRgb(0xF2, 0xC8, 0x11), // 4
        Color.FromRgb(0x10, 0x7C, 0x10), // 5
        Color.FromRgb(0x00, 0xB7, 0xC3), // 6
        Color.FromRgb(0x00, 0x78, 0xD4), // 7
        Color.FromRgb(0x87, 0x64, 0xB8), // 8
        Color.FromRgb(0x73, 0x73, 0x73), // 9
    ];

    private readonly RadioButton[] _colorRings;
    private bool _stateSync;
    private bool _penKindSync;
    private int _selectedPaletteIndex;

    private Slider PenThickness => (Slider)PenThicknessSlider!;
    private TextBlock PenThicknessText => (TextBlock)PenThicknessValueText!;

    public Color SelectedColor { get; private set; }
    public double SelectedThickness { get; private set; } = 4;
    public PenKind SelectedKind { get; private set; } = PenKind.Pen;

    public event Action<Color>? PenColorChanged;
    public event Action<double>? PenThicknessChanged;
    public event Action<PenKind>? PenKindChanged;
    public event Action? DismissRequested;

    public PenSecondaryMenuWindow()
    {
        SelectedColor = PaletteColors[0];

        AllowsTransparency = true;
        ShowActivated = false;
        InitializeComponent();
        SystemBackdrop = WindowBackdropType.None;
        Background = TransparentBrush;
        Opacity = 1;

        _colorRings =
        [
            (RadioButton)PenColorRing1!,
            (RadioButton)PenColorRing2!,
            (RadioButton)PenColorRing3!,
            (RadioButton)PenColorRing4!,
            (RadioButton)PenColorRing5!,
            (RadioButton)PenColorRing6!,
            (RadioButton)PenColorRing7!,
            (RadioButton)PenColorRing8!,
            (RadioButton)PenColorRing9!,
        ];

        _selectedPaletteIndex = 0;
        UpdateColorSelectionVisuals();
        PenThickness.Value = SelectedThickness;
        UpdateThicknessText();

        WireColorRings();
        WirePenKindRadios();
        PenThickness.ValueChanged += PenThicknessSlider_OnValueChanged;
        AutomationProperties.SetName(PenThickness, "画笔粗细");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            DismissRequested?.Invoke();
            e.Handled = true;
        };
        Loaded += (_, _) => FluentTheme.ApplyMotionPolicy(this);

        FitSizeToContent();
    }

    /// <summary>
    /// 26.10.x 中 <see cref="Window.SizeToContent"/> 尚未被框架接线（Window.MeasureOverride
    /// 直接返回可用尺寸），标记 WidthAndHeight 会让内容被排列到默认 800×600，
    /// 白色卡片 Border 被随之拉伸。改为 Show 前手动按内容 DesiredSize 定窗口尺寸。
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

    public void SetCurrentState(Color color, double thickness, PenKind kind)
    {
        _stateSync = true;
        _penKindSync = true;
        try
        {
            SelectedColor = color;
            SelectedThickness = double.IsFinite(thickness) ? Math.Clamp(Math.Round(thickness), 1, 24) : 4;
            SelectedKind = kind;
            PenThickness.Value = SelectedThickness;
            UpdateThicknessText();
            _selectedPaletteIndex = FindBestPaletteIndex(color);
            UpdateColorSelectionVisuals();
            PenKindPenRadio!.IsChecked = kind == PenKind.Pen;
            PenKindHighlighterRadio!.IsChecked = kind == PenKind.Highlighter;
            PenKindLaserRadio!.IsChecked = kind == PenKind.Laser;
        }
        finally
        {
            _penKindSync = false;
            _stateSync = false;
        }
    }

    private static int FindBestPaletteIndex(Color c)
    {
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < PaletteColors.Length; i++)
        {
            var p = PaletteColors[i];
            var dr = c.R - p.R;
            var dg = c.G - p.G;
            var db = c.B - p.B;
            var d = (dr * dr) + (dg * dg) + (db * db);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    private void WireColorRings()
    {
        string[] names = ["黑色", "红色", "橙色", "黄色", "绿色", "青色", "蓝色", "紫色", "灰色"];
        for (var i = 0; i < _colorRings.Length; i++)
        {
            var ring = _colorRings[i];
            var index = i;
            AutomationProperties.SetName(ring, names[i]);
            ring.Checked += (_, _) =>
            {
                if (!_stateSync) SelectPaletteIndex(index);
            };
            ring.PreviewKeyDown += (_, e) =>
            {
                if (e.KeyboardModifiers != ModifierKeys.None) return;
                var next = e.Key switch
                {
                    Key.Left => index % 3 == 0 ? index + 2 : index - 1,
                    Key.Right => index % 3 == 2 ? index - 2 : index + 1,
                    Key.Up => (index + 6) % 9,
                    Key.Down => (index + 3) % 9,
                    Key.Home => 0,
                    Key.End => 8,
                    _ => -1,
                };
                if (next < 0) return;
                _colorRings[next].Focus();
                _colorRings[next].IsChecked = true;
                e.Handled = true;
            };
        }
    }

    internal void FocusSelectedColor() => _colorRings[_selectedPaletteIndex].Focus();

    private void WirePenKindRadios()
    {
        PenKindPenRadio!.Checked += OnAnyPenKindRadioChecked;
        PenKindHighlighterRadio!.Checked += OnAnyPenKindRadioChecked;
        PenKindLaserRadio!.Checked += OnAnyPenKindRadioChecked;
    }

    private void OnAnyPenKindRadioChecked(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (_penKindSync)
            return;

        if (sender is not RadioButton rb || rb.IsChecked != true)
            return;

        var kind = ReferenceEquals(rb, PenKindHighlighterRadio)
            ? PenKind.Highlighter
            : ReferenceEquals(rb, PenKindLaserRadio)
                ? PenKind.Laser
                : PenKind.Pen;

        if (SelectedKind == kind)
            return;

        SelectedKind = kind;
        PenKindChanged?.Invoke(kind);
    }

    private void SelectPaletteIndex(int index)
    {
        if (index < 0 || index >= PaletteColors.Length)
            return;

        _selectedPaletteIndex = index;
        var c = PaletteColors[index];
        UpdateColorSelectionVisuals();
        SelectedColor = c;
        PenColorChanged?.Invoke(c);
    }

    private void UpdateColorSelectionVisuals()
    {
        var previousSync = _stateSync;
        _stateSync = true;
        try
        {
            for (var i = 0; i < _colorRings.Length; i++)
                _colorRings[i].IsChecked = i == _selectedPaletteIndex;
        }
        finally { _stateSync = previousSync; }
    }

    private void UpdateThicknessText()
    {
        PenThicknessText.Text = $"{(int)Math.Round(SelectedThickness)} px";
    }

    private void PenThicknessSlider_OnValueChanged(object sender, RoutedEventArgs e)
    {
        if (_stateSync) return;
        _ = sender;
        _ = e;
        SelectedThickness = Math.Round(PenThickness.Value);
        UpdateThicknessText();
        PenThicknessChanged?.Invoke(SelectedThickness);
    }
}
