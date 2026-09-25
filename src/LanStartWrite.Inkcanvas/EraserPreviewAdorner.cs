using Jalium.UI;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

internal sealed class EraserPreviewAdorner : FrameworkElement
{
    private static readonly SvgImage EraserImage = SvgImage.FromSvgString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\" viewBox=\"0 0 64 64\">" +
        "<path d=\"M12 39L34 17Q36 15 38 17L51 30Q53 32 51 34L29 56L12 39Z\" fill=\"#F8FAFC\" stroke=\"#1F2937\" stroke-width=\"3\" stroke-linejoin=\"round\"/>" +
        "<path d=\"M12 39L29 56L22 61L6 45L12 39Z\" fill=\"#CBD5E1\" stroke=\"#1F2937\" stroke-width=\"3\" stroke-linejoin=\"round\"/>" +
        "<path d=\"M34 17L42 9Q44 7 46 9L58 21Q60 23 58 25L51 32L34 17Z\" fill=\"#94A3B8\" stroke=\"#1F2937\" stroke-width=\"3\" stroke-linejoin=\"round\"/>" +
        "<path d=\"M19 38L35 22\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"3\" stroke-linecap=\"round\" opacity=\".9\"/>" +
        "</svg>");

    private static readonly Brush RangeFill = new SolidColorBrush(Color.FromArgb(0x38, 0x0A, 0x78, 0xD4));
    private static readonly Pen RangePen = new(new SolidColorBrush(Color.FromArgb(0xD0, 0x0A, 0x78, 0xD4)), 1.25);
    private static readonly Pen ContactPen = new(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1);

    private bool _visible;
    private Point _center;
    private double _radius;

    internal EraserPreviewAdorner()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    internal void Show(Point center, double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0)
        {
            Hide();
            return;
        }

        bool changed = !_visible || _center != center || !_radius.Equals(radius);
        _center = center;
        _radius = radius;
        if (changed)
        {
            _visible = true;
            InvalidateVisual();
        }
    }

    internal bool PreviewVisible => _visible;

    internal Point? Center => _visible ? _center : null;

    internal double Radius => _radius;

    internal void SetRadius(double radius)
    {
        if (_radius.Equals(radius)) return;
        _radius = radius;
        if (_visible) InvalidateVisual();
    }

    internal void Hide()
    {
        if (!_visible) return;
        _visible = false;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext context)
    {
        if (!_visible || _radius <= 0) return;

        context.DrawEllipse(RangeFill, RangePen, _center, _radius, _radius);
        context.DrawEllipse(null, ContactPen, _center, Math.Max(1, _radius - 1), Math.Max(1, _radius - 1));

        var iconSize = Math.Min(_radius * 1.5, _radius * 2 - 2);
        var iconRect = new Rect(
            _center.X - iconSize / 2,
            _center.Y - iconSize / 2,
            iconSize,
            iconSize);
        context.DrawImage(EraserImage, iconRect);
    }
}
