using Dusk.Ink.Document;
using Dusk.Ink.Model;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

internal sealed class WhiteboardThumbnail : FrameworkElement
{
    private readonly InkDocument _document;
    private Color _pageBackground;

    internal WhiteboardThumbnail(InkDocument document)
    {
        _document = document;
        _pageBackground = Colors.White;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    internal Color PageBackground
    {
        get => _pageBackground;
        set
        {
            if (_pageBackground == value) return;
            _pageBackground = value;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    internal void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext context)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        context.DrawRectangle(new SolidColorBrush(_pageBackground), null, new Rect(size));
        var bounds = _document.GetBounds();
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rect2D(0, 0, 16, 9);

        var padding = Math.Max(bounds.Width, bounds.Height) * 0.04 + 0.5;
        bounds = bounds.Inflate(padding, padding);
        var scale = Math.Min(size.Width / bounds.Width, size.Height / bounds.Height);
        var offsetX = (size.Width - (bounds.Width * scale)) / 2 - (bounds.Left * scale);
        var offsetY = (size.Height - (bounds.Height * scale)) / 2 - (bounds.Top * scale);

        foreach (var stroke in _document.Strokes)
        {
            if (stroke.PointCount == 0) continue;
            var color = stroke.Attributes.Color;
            var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            var pen = new Pen(brush, Math.Max(0.5, stroke.Attributes.Width * scale));
            if (stroke.PointCount == 1)
            {
                var point = stroke.Points[0].Position;
                context.DrawEllipse(brush, null, Map(point.X, point.Y, bounds, scale, offsetX, offsetY), pen.Thickness / 2, pen.Thickness / 2);
                continue;
            }

            for (var i = 1; i < stroke.PointCount; i++)
            {
                var previous = stroke.Points[i - 1].Position;
                var current = stroke.Points[i].Position;
                context.DrawLine(pen,
                    Map(previous.X, previous.Y, bounds, scale, offsetX, offsetY),
                    Map(current.X, current.Y, bounds, scale, offsetX, offsetY));
            }
        }
    }

    private static Point Map(double x, double y, Rect2D bounds, double scale, double offsetX, double offsetY) =>
        new(x * scale + offsetX, y * scale + offsetY);
}
