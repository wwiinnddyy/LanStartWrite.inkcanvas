using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationToolbarWindow : Window
{
    private bool _toolSync;

    /// <summary>由 .g.cs 装入的 <c>x:Name</c> 为 <see cref="Jalium.UI.FrameworkElement"/>，此处转为具体控件类型。</summary>
    private AppBarToggleButton MouseTool => (AppBarToggleButton)MouseToolToggle!;
    private AppBarToggleButton PenTool => (AppBarToggleButton)PenToolToggle!;
    private AppBarToggleButton EraseTool => (AppBarToggleButton)EraseToolToggle!;
    private AppBarButton SettingsTool => (AppBarButton)SettingsToolbarButton!;

    public AnnotationToolbarWindow()
    {
        InitializeComponent();

        WireTools();
        ApplyToolbarIcons();
        WireDragHandle();

        _toolSync = true;
        MouseTool.IsChecked = true;
        PenTool.IsChecked = false;
        EraseTool.IsChecked = false;
        _toolSync = false;
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
        MouseTool.Icon = new SymbolIcon(Symbol.TouchPointer);
        PenTool.Icon = new SymbolIcon(Symbol.InkingTool);
        EraseTool.Icon = new SymbolIcon(Symbol.EraseTool);
        SettingsTool.Icon = new SymbolIcon(Symbol.Settings);
        var grip = new SymbolIcon(Symbol.GripperBarVertical);
        grip.IsHitTestVisible = false;
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
    }
}
