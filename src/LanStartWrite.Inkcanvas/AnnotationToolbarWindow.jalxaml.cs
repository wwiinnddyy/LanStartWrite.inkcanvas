using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationToolbarWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    private static readonly Color PenBlack = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color PenRed = Color.FromRgb(0xD1, 0x34, 0x38);
    private static readonly Color PenBlue = Color.FromRgb(0x00, 0x78, 0xD4);
    private static readonly Color PenGreen = Color.FromRgb(0x10, 0x7C, 0x10);

    private bool _toolSync;
    private AnnotationOverlayWindow? _annotationOverlay;
    private bool _annotationOverlayVisible;
    private SettingsWindow? _settingsWindow;
    private PenSecondaryMenuWindow? _penMenuWindow;
    private bool _penMenuVisible;
    private Color _currentPenColor = PenBlack;
    private double _currentPenThickness = 4;
    private PenKind _currentPenKind = PenKind.Pen;

    /// <summary>由 .g.cs 装入的 <c>x:Name</c> 为 <see cref="Jalium.UI.FrameworkElement"/>，此处转为具体控件类型。</summary>
    private RadioToolToggleButton MouseTool => (RadioToolToggleButton)MouseToolToggle!;
    private RadioToolToggleButton PenTool => (RadioToolToggleButton)PenToolToggle!;
    private RadioToolToggleButton EraseTool => (RadioToolToggleButton)EraseToolToggle!;
    private AppBarButton SettingsTool => (AppBarButton)SettingsToolbarButton!;

    public AnnotationToolbarWindow()
    {
        AllowsTransparency = true;
        InitializeComponent();
        SystemBackdrop = WindowBackdropType.None;
        Background = TransparentBrush;
        Opacity = 1;

        WireTools();
        ApplyToolbarIcons();
        WireDragHandle();
        WireSecondaryToolTriggers();
        Closed += (_, _) =>
        {
            _penMenuWindow?.Close();
            _penMenuWindow = null;
            _penMenuVisible = false;
            _settingsWindow?.Close();
            _settingsWindow = null;
            DisposeAnnotationOverlay();
        };
        Loaded += (_, _) => Topmost = true;

        _toolSync = true;
        MouseTool.IsChecked = true;
        PenTool.IsChecked = false;
        EraseTool.IsChecked = false;
        _toolSync = false;
        SyncAnnotationOverlay();
    }

    private void WireTools()
    {
        MouseTool.Checked += OnToolToggleChecked;
        PenTool.Checked += OnToolToggleChecked;
        EraseTool.Checked += OnToolToggleChecked;
    }

    private void OnToolToggleChecked(object sender, RoutedEventArgs e)
    {
        if (_toolSync || sender is not RadioToolToggleButton active || active.IsChecked != true)
            return;

        _toolSync = true;
        try
        {
            foreach (var t in AllDrawingTools())
                t.IsChecked = ReferenceEquals(t, active);
        }
        finally
        {
            _toolSync = false;
        }

        SyncAnnotationOverlay();
    }

    private void EnsureAnnotationOverlay()
    {
        _annotationOverlay ??= new AnnotationOverlayWindow();
    }

    private void DisposeAnnotationOverlay()
    {
        if (_annotationOverlay is null)
            return;
        _annotationOverlay.Close();
        _annotationOverlay = null;
    }

    /// <summary>「鼠标」收起透明画布；「笔」显示并墨迹；「橡皮」仅在画布当前处于打开状态时切换擦除模式。</summary>
    private void SyncAnnotationOverlay()
    {
        if (MouseTool.IsChecked == true)
        {
            _annotationOverlay?.Hide();
            _annotationOverlayVisible = false;
            HidePenSecondaryMenu();
            return;
        }

        if (PenTool.IsChecked == true)
        {
            EnsureAnnotationOverlay();
            _annotationOverlay!.SetInkMode();
            _annotationOverlay.SetPenKind(_currentPenKind);
            _annotationOverlay.SetPenColor(_currentPenColor);
            _annotationOverlay.SetPenThickness(_currentPenThickness);
            _annotationOverlay.Show();
            RaiseToolbarAboveAnnotationOverlay();
            _annotationOverlayVisible = true;
            return;
        }

        if (EraseTool.IsChecked == true)
        {
            HidePenSecondaryMenu();
            if (!_annotationOverlayVisible || _annotationOverlay is null)
                return;
            _annotationOverlay.SetEraseMode();
            _annotationOverlay.Show();
            RaiseToolbarAboveAnnotationOverlay();
        }
    }

    /// <summary>
    /// 全屏画布 Show 后会参与 Z 序与前台；刷新批注栏 Topmost 并激活批注栏，避免被盖住或失去「总在最前」。
    /// </summary>
    private void RaiseToolbarAboveAnnotationOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var t = Topmost;
            Topmost = false;
            Topmost = t;
            Activate();
            if (_penMenuWindow is not null)
                _penMenuWindow.Topmost = Topmost;
        });
    }

    private IEnumerable<RadioToolToggleButton> AllDrawingTools()
    {
        yield return MouseTool;
        yield return PenTool;
        yield return EraseTool;
    }

    /// <summary>使用 Jalium 自带的 <see cref="SymbolIcon"/> + <see cref="Symbol"/>（Segoe Fluent Icons 码位由框架维护）。</summary>
    private void ApplyToolbarIcons()
    {
        var iconFg = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
        MouseTool.Icon = new SymbolIcon(Symbol.TouchPointer) { Foreground = iconFg };
        PenTool.Icon = new SymbolIcon(Symbol.InkingTool) { Foreground = iconFg };
        EraseTool.Icon = new SymbolIcon(Symbol.EraseTool) { Foreground = iconFg };
        SettingsTool.Icon = new SymbolIcon(Symbol.Settings) { Foreground = iconFg };
        var grip = new SymbolIcon(Symbol.GripperBarVertical)
        {
            IsHitTestVisible = false,
            Foreground = iconFg,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DragHandleChrome!.Child = grip;
    }

    private void WireDragHandle()
    {
        var h = DragHandleChrome!;
        h.PreviewPointerDown += DragHandle_OnPreviewPointerDown;
    }

    private void WireSecondaryToolTriggers()
    {
        PenTool.Reactivated += (_, _) => TogglePenSecondaryMenu();
    }

    private void EnsurePenSecondaryMenuWindow()
    {
        if (_penMenuWindow is not null)
            return;

        _penMenuWindow = new PenSecondaryMenuWindow();
        _penMenuWindow.SetCurrentState(_currentPenColor, _currentPenThickness, _currentPenKind);
        _penMenuWindow.PenColorChanged += c =>
        {
            _currentPenColor = c;
            ApplyPenSettingsToOverlay();
        };
        _penMenuWindow.PenThicknessChanged += t =>
        {
            _currentPenThickness = t;
            ApplyPenSettingsToOverlay();
        };
        _penMenuWindow.PenKindChanged += k =>
        {
            _currentPenKind = k;
            ApplyPenSettingsToOverlay();
        };
        _penMenuWindow.Closed += (_, _) =>
        {
            _penMenuWindow = null;
            _penMenuVisible = false;
        };
    }

    private void PositionPenSecondaryMenu()
    {
        if (_penMenuWindow is null)
            return;

        _penMenuWindow.Left = Left + 4;
        _penMenuWindow.Top = Top + Height + 2;
        _penMenuWindow.Topmost = Topmost;
    }

    private void ShowPenSecondaryMenu()
    {
        if (PenTool.IsChecked != true)
            return;

        EnsurePenSecondaryMenuWindow();
        _penMenuWindow!.SetCurrentState(_currentPenColor, _currentPenThickness, _currentPenKind);
        PositionPenSecondaryMenu();
        _penMenuWindow.Show();
        _penMenuVisible = true;
    }

    private void HidePenSecondaryMenu()
    {
        _penMenuWindow?.Hide();
        _penMenuVisible = false;
    }

    private void TogglePenSecondaryMenu()
    {
        if (PenTool.IsChecked != true)
            return;

        if (_penMenuWindow is not null && _penMenuVisible)
        {
            _penMenuWindow.Hide();
            _penMenuVisible = false;
            return;
        }

        ShowPenSecondaryMenu();
    }

    private void ApplyPenSettingsToOverlay()
    {
        if (_annotationOverlay is null)
            return;
        _annotationOverlay.SetPenKind(_currentPenKind);
        _annotationOverlay.SetPenColor(_currentPenColor);
        _annotationOverlay.SetPenThickness(_currentPenThickness);
    }

    private void DragHandle_OnPreviewPointerDown(object sender, RoutedEventArgs e)
    {
        if (e is not PointerDownEventArgs p || sender is not FrameworkElement fe)
            return;

        var pt = p.GetCurrentPoint(fe);
        var props = pt.Properties;
        var ok = pt.PointerDeviceType switch
        {
            PointerDeviceType.Mouse => props.IsLeftButtonPressed
                || props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed,
            PointerDeviceType.Touch => pt.IsInContact,
            PointerDeviceType.Pen => pt.IsInContact && !props.IsEraser,
            _ => false,
        };
        if (!ok)
            return;

        p.Handled = true;
        DragMove();
        PositionPenSecondaryMenu();
    }

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

        var prevToolbarTopmost = Topmost;
        Topmost = false;
        if (_penMenuWindow is not null)
            _penMenuWindow.Topmost = false;
        HidePenSecondaryMenu();
        // 设置页期间彻底关闭全屏透明画布，避免透明窗口/置顶状态拦截输入。
        DisposeAnnotationOverlay();
        _annotationOverlayVisible = false;

        w.Closed += (_, _) =>
        {
            _settingsWindow = null;
            Topmost = prevToolbarTopmost;
            if (_penMenuWindow is not null)
                _penMenuWindow.Topmost = prevToolbarTopmost;
            SyncAnnotationOverlay();
        };

        w.Show();
        w.Activate();
    }
}
