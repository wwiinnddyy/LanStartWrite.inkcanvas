using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationToolbarWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    private static Brush ToolbarIconBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush ToolbarIconOnAccentBrush => FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush");
    private static readonly Color PenBlack = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color PenRed = Color.FromRgb(0xD1, 0x34, 0x38);
    private static readonly Color PenBlue = Color.FromRgb(0x00, 0x78, 0xD4);
    private static readonly Color PenGreen = Color.FromRgb(0x10, 0x7C, 0x10);

    private bool _toolSync;
    private bool _isClosing;
    private AnnotationOverlayWindow? _annotationOverlay;
    private SettingsWindow? _settingsWindow;
    private PenSecondaryMenuWindow? _penMenuWindow;
    private bool _penMenuVisible;
    private TouchDevice? _touchDragDevice;
    private Point _touchDragStartScreenPoint;
    private double _touchDragStartWindowLeft;
    private double _touchDragStartWindowTop;
    private Color _currentPenColor = PenBlack;
    private double _currentPenThickness = 4;
    private PenKind _currentPenKind = PenKind.Pen;

    /// <summary>由 .g.cs 装入的 <c>x:Name</c> 为 <see cref="Jalium.UI.FrameworkElement"/>，此处转为具体控件类型。</summary>
    private RadioToolToggleButton MouseTool => (RadioToolToggleButton)MouseToolToggle!;
    private RadioToolToggleButton PenTool => (RadioToolToggleButton)PenToolToggle!;
    private RadioToolToggleButton EraseTool => (RadioToolToggleButton)EraseToolToggle!;
    private AppBarButton SettingsTool => (AppBarButton)SettingsToolbarButton!;
    private Border DragHandle => (Border)DragHandleChrome!;

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
        FitSizeToContent();
        _currentPenThickness = AppPreferences.Current.PenWidth;
        AppPreferences.Changed += OnPreferencesChanged;
        FluentTheme.Changed += SyncToolbarIconForegrounds;
        LocationChanged += (_, _) => { if (_penMenuVisible) PositionPenSecondaryMenu(); };
        Hiding += (_, _) => EndTouchDrag(DragHandle);
        SystemSettingsChanged += (_, _) => FluentTheme.ApplyPreferences();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_penMenuVisible) HidePenSecondaryMenu();
            else MouseTool.IsChecked = true;
            e.Handled = true;
        };
        Closed += (_, _) =>
        {
            _isClosing = true;
            EndTouchDrag(DragHandle);
            AppPreferences.Changed -= OnPreferencesChanged;
            FluentTheme.Changed -= SyncToolbarIconForegrounds;
            _penMenuWindow?.Close();
            _penMenuWindow = null;
            _penMenuVisible = false;
            _settingsWindow?.Close();
            _settingsWindow = null;
            DisposeAnnotationOverlay();
        };
        Loaded += (_, _) =>
        {
            ApplyTopmostPolicy();
            FluentTheme.ApplyMotionPolicy(this);
        };

        _toolSync = true;
        MouseTool.IsChecked = true;
        PenTool.IsChecked = false;
        EraseTool.IsChecked = false;
        _toolSync = false;
        SyncToolbarIconForegrounds();
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
        Width = Math.Max(1, Math.Ceiling(desired.Width));
        Height = Math.Max(1, Math.Ceiling(desired.Height));
    }

    private void WireTools()
    {
        MouseTool.Checked += OnToolToggleChecked;
        PenTool.Checked += OnToolToggleChecked;
        EraseTool.Checked += OnToolToggleChecked;
        foreach (var control in new Control[] { MouseTool, PenTool, EraseTool, SettingsTool })
            control.PreviewKeyDown += Tool_OnPreviewKeyDown;
    }

    private void Tool_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ReferenceEquals(sender, PenTool) && e.Key == Key.Down &&
            (e.KeyboardModifiers == ModifierKeys.None || e.KeyboardModifiers == ModifierKeys.Alt))
        {
            PenTool.IsChecked = true;
            ShowPenSecondaryMenu(focus: true);
            e.Handled = true;
            return;
        }
        if (e.KeyboardModifiers != ModifierKeys.None) return;
        Control[] tools = [MouseTool, PenTool, EraseTool, SettingsTool];
        var current = Array.FindIndex(tools, control => ReferenceEquals(control, sender));
        if (current < 0) return;
        var next = e.Key switch
        {
            Key.Right => (current + 1) % tools.Length,
            Key.Left => (current + tools.Length - 1) % tools.Length,
            Key.Home => 0,
            Key.End => tools.Length - 1,
            _ => -1,
        };
        if (next < 0) return;
        tools[next].Focus();
        e.Handled = true;
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

        SyncToolbarIconForegrounds();
        SyncAnnotationOverlay();
    }

    private void SyncToolbarIconForegrounds()
    {
        if (MouseTool.Icon is SymbolIcon mouseIcon)
            mouseIcon.Foreground = MouseTool.IsChecked == true ? ToolbarIconOnAccentBrush : ToolbarIconBrush;
        if (PenTool.Icon is SymbolIcon penIcon)
            penIcon.Foreground = PenTool.IsChecked == true ? ToolbarIconOnAccentBrush : ToolbarIconBrush;
        if (EraseTool.Icon is SymbolIcon eraseIcon)
            eraseIcon.Foreground = EraseTool.IsChecked == true ? ToolbarIconOnAccentBrush : ToolbarIconBrush;
        if (SettingsTool.Icon is SymbolIcon settingsIcon)
            settingsIcon.Foreground = ToolbarIconBrush;
    }

    private void EnsureAnnotationOverlay()
    {
        if (_annotationOverlay is not null) return;
        _annotationOverlay = new AnnotationOverlayWindow();
        _annotationOverlay.PreviewPointerDown += (_, _) => HidePenSecondaryMenu();
    }

    private void DisposeAnnotationOverlay()
    {
        if (_annotationOverlay is null)
            return;
        _annotationOverlay.Close();
        _annotationOverlay = null;
    }

    /// <summary>鼠标模式隐藏画布；笔与橡皮显示同一画布并切换编辑模式。</summary>
    private void SyncAnnotationOverlay()
    {
        if (_isClosing) return;
        if (_settingsWindow is not null)
        {
            _annotationOverlay?.Hide();
            HidePenSecondaryMenu();
            return;
        }
        if (MouseTool.IsChecked == true)
        {
            _annotationOverlay?.Hide();
            HidePenSecondaryMenu();
            ApplyTopmostPolicy();
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
            return;
        }

        if (EraseTool.IsChecked == true)
        {
            HidePenSecondaryMenu();
            EnsureAnnotationOverlay();
            _annotationOverlay!.SetEraseMode();
            _annotationOverlay.Show();
            RaiseToolbarAboveAnnotationOverlay();
        }
    }

    /// <summary>
    /// 全屏画布 Show 后会参与 Z 序与前台；刷新批注栏 Topmost 并激活批注栏，避免被盖住或失去「总在最前」。
    /// </summary>
    private void RaiseToolbarAboveAnnotationOverlay()
    {
        // 全屏透明 InkCanvas 必须始终停留在普通 Z 序，工具栏则保持 Topmost。
        // Jalium 的透明窗口 Show() 在首帧提交时可能再次调整 native Z 序，
        // 因此先同步提升一次，再在当前 Dispatcher 队列末尾补一次，避免画布抢占输入。
        if (_annotationOverlay is not null)
            _annotationOverlay.Topmost = false;

        Topmost = false;
        Topmost = true;
        Activate();

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || _settingsWindow is not null) return;
            if (_annotationOverlay is not null)
                _annotationOverlay.Topmost = false;

            Topmost = false;
            Topmost = true;
            Activate();
            if (_penMenuWindow is not null)
                _penMenuWindow.Topmost = true;
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
        // 前景色交由 AppBar 样式按 Checked/Hover 状态控制，这里不设硬编码画刷。
        MouseTool.Icon = new SymbolIcon(Symbol.TouchPointer) { IsHitTestVisible = false, Width = 16, Height = 16 };
        PenTool.Icon = new SymbolIcon(Symbol.InkingTool) { IsHitTestVisible = false, Width = 16, Height = 16 };
        EraseTool.Icon = new SymbolIcon(Symbol.EraseTool) { IsHitTestVisible = false, Width = 16, Height = 16 };
        SettingsTool.Icon = new SymbolIcon(Symbol.Settings) { IsHitTestVisible = false, Width = 16, Height = 16 };
        AutomationProperties.SetName(MouseTool, "鼠标模式");
        AutomationProperties.SetName(PenTool, "笔；再次点击打开笔设置");
        AutomationProperties.SetName(EraseTool, "橡皮");
        AutomationProperties.SetName(SettingsTool, "设置");
        MouseTool.ToolTip = "鼠标模式";
        PenTool.ToolTip = "笔 · 再次点击打开笔设置";
        EraseTool.ToolTip = "橡皮";
        SettingsTool.ToolTip = "设置";
    }

    private void WireDragHandle()
    {
        var h = DragHandle;
        h.PreviewPointerDown += DragHandle_OnPreviewPointerDown;
        h.PreviewTouchDown += DragHandle_OnPreviewTouchDown;
        h.PreviewTouchMove += DragHandle_OnPreviewTouchMove;
        h.PreviewTouchUp += DragHandle_OnPreviewTouchUp;
        h.LostTouchCapture += DragHandle_OnLostTouchCapture;
    }

    private void WireSecondaryToolTriggers()
    {
        PenTool.Reactivated += (_, _) => TogglePenSecondaryMenu();
    }

    private void EnsurePenSecondaryMenuWindow()
    {
        if (_penMenuWindow is not null)
            return;

        _penMenuWindow = new PenSecondaryMenuWindow { Owner = this };
        _penMenuWindow.DismissRequested += () => { HidePenSecondaryMenu(); Activate(); PenTool.Focus(); };
        _penMenuWindow.SetCurrentState(_currentPenColor, _currentPenThickness, _currentPenKind);
        _penMenuWindow.PenColorChanged += c =>
        {
            _currentPenColor = c;
            ApplyPenSettingsToOverlay();
        };
        _penMenuWindow.PenThicknessChanged += t =>
        {
            _currentPenThickness = t;
            AppPreferences.Update(AppPreferences.Current with { PenWidth = t });
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

        if (!FlyoutPlacement.Position(this, _penMenuWindow))
        {
            _penMenuWindow.Left = Left;
            _penMenuWindow.Top = Top + Height - 8;
        }
        _penMenuWindow.Topmost = Topmost;
    }

    private void ShowPenSecondaryMenu(bool focus = false)
    {
        if (PenTool.IsChecked != true)
            return;

        EnsurePenSecondaryMenuWindow();
        _penMenuWindow!.SetCurrentState(_currentPenColor, _currentPenThickness, _currentPenKind);
        PositionPenSecondaryMenu();
        _penMenuWindow.Show();
        PositionPenSecondaryMenu();
        _penMenuVisible = true;
        FluentTheme.Enter(_penMenuWindow.Content as UIElement ?? _penMenuWindow);
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

        Topmost = false;
        if (_penMenuWindow is not null)
            _penMenuWindow.Topmost = false;
        HidePenSecondaryMenu();
        // Hide suspends input without destroying the user's existing strokes.
        _annotationOverlay?.Hide();

        w.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_isClosing) return;
            ApplyTopmostPolicy();
            SyncAnnotationOverlay();
        };

        w.Show();
        w.Activate();
    }

    private void ApplyTopmostPolicy()
    {
        Topmost = _settingsWindow is null &&
            (MouseTool.IsChecked != true || AppPreferences.Current.KeepToolbarOnTop);
        if (_penMenuWindow is not null) _penMenuWindow.Topmost = Topmost;
    }

    private void OnPreferencesChanged(PreferenceSnapshot value)
    {
        if (_currentPenThickness != value.PenWidth)
        {
            _currentPenThickness = value.PenWidth;
            _penMenuWindow?.SetCurrentState(_currentPenColor, _currentPenThickness, _currentPenKind);
            ApplyPenSettingsToOverlay();
        }
        ApplyTopmostPolicy();
    }
}
