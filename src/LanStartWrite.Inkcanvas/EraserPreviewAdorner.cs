using Jalium.UI;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

internal sealed class EraserPreviewAdorner : FrameworkElement
{
    private static readonly SvgImage EraserImage = SvgImage.FromSvgString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\" viewBox=\"0 0 64 64\">" +
        "<path d=\"M13 20L39 8L53 14L27 27Z\" fill=\"#F2C078\" stroke=\"#20252B\" stroke-width=\"3\" stroke-linejoin=\"round\"/>" +
        "<path d=\"M13 20L27 27L27 47L13 40Z\" fill=\"#B97845\" stroke=\"#20252B\" stroke-width=\"3\" stroke-linejoin=\"round\"/>" +
        "<path d=\"M27 27L53 14L53 34L27 47Z\" fill=\"#D99A5B\" stroke=\"#20252B\" stroke-width=\"3\" stroke-linejoin=\"round\"/>" +
        "<path d=\"M31 29L49 20\" fill=\"none\" stroke=\"#FFF0C7\" stroke-width=\"3\" stroke-linecap=\"round\" opacity=\".8\"/>" +
        "<path d=\"M12 49L51 33\" fill=\"none\" stroke=\"#20252B\" stroke-width=\"5\" stroke-linecap=\"round\" opacity=\".85\"/>" +
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

        _center = center;
        _radius = radius;
        if (!_visible)
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
