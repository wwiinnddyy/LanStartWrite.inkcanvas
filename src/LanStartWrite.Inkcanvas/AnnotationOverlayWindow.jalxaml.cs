using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Ink;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationOverlayWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private InkCanvas Surface => (InkCanvas)OverlayInk!;

    public AnnotationOverlayWindow()
    {
        AllowsTransparency = true;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        Opacity = 1;
        Background = TransparentBrush;
        InitializeComponent();
        Surface.Background = TransparentBrush;
        ApplyDefaultDrawingAttributes();
        Surface.EditingMode = InkCanvasEditingMode.Ink;
    }

    private void ApplyDefaultDrawingAttributes()
    {
        var da = Surface.DefaultDrawingAttributes;
        da.Color = Colors.Black;
        da.Width = 4;
        da.Height = 4;
        da.BrushType = BrushType.Pen;
        da.IgnorePressure = true;
        da.IsHighlighter = false;
    }

    public void SetInkMode()
    {
        Surface.EditingMode = InkCanvasEditingMode.Ink;
    }

    public void SetEraseMode()
    {
        Surface.EditingMode = InkCanvasEditingMode.EraseByStroke;
    }
}
