using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationToolbarWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private bool _toolSync;
    private AnnotationOverlayWindow? _annotationOverlay;
    private bool _annotationOverlayVisible;
    private SettingsWindow? _settingsWindow;

    /// <summary>由 .g.cs 装入的 <c>x:Name</c> 为 <see cref="Jalium.UI.FrameworkElement"/>，此处转为具体控件类型。</summary>
    private AppBarToggleButton MouseTool => (AppBarToggleButton)MouseToolToggle!;
    private AppBarToggleButton PenTool => (AppBarToggleButton)PenToolToggle!;
    private AppBarToggleButton EraseTool => (AppBarToggleButton)EraseToolToggle!;
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
        Closed += (_, _) =>
        {
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
        if (_toolSync || sender is not AppBarToggleButton active || active.IsChecked != true)
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
            return;
        }

        if (PenTool.IsChecked == true)
        {
            EnsureAnnotationOverlay();
            _annotationOverlay!.SetInkMode();
            _annotationOverlay.Show();
            RaiseToolbarAboveAnnotationOverlay();
            _annotationOverlayVisible = true;
            return;
        }

        if (EraseTool.IsChecked == true)
        {
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
        });
    }

    private IEnumerable<AppBarToggleButton> AllDrawingTools()
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
        w.Closed += (_, _) => _settingsWindow = null;
        w.Show();
    }
}
