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
    private Brush? _pageBrush;
    private Size? _contentSize;

    internal WhiteboardThumbnail(InkDocument document)
    {
        _document = document;
        _pageBackground = Colors.White;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>
    /// 这一页底下铺什么。<b>纯色与图是同一件事的两种形态</b>，所以谁都不比谁更"底"：
    /// 白板给一个 <see cref="PageBackground"/>，图片批注给一张铺满的 <see cref="PageBrush"/>。
    /// <para>给了 <see cref="PageBrush"/> 就以它为准 —— 少一条"两个都设了听谁的"的分支。</para>
    /// </summary>
    internal Brush? PageBrush
    {
        get => _pageBrush;
        set
        {
            if (ReferenceEquals(_pageBrush, value)) return;
            _pageBrush = value;
            InvalidateVisual();
        }
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

    /// <summary>
    /// 这一页的<b>内容尺寸</b>（世界单位），<c>null</c> 表示"用墨迹自己的包围盒"。
    /// <para>
    /// 为什么需要它：图片页上墨迹是画在图里的，缩略图必须按<b>图的大小</b>定位笔迹，
    /// 而不是按笔迹的包围盒 —— 否则用户只在图的左上角画了一笔，缩略图就会把那一笔放大到满格，
    /// 看上去像"那一笔占满了整张图"。
    /// </para>
    /// </summary>
    internal Size? ContentSize
    {
        get => _contentSize;
        set
        {
            if (_contentSize == value) return;
            _contentSize = value;
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

        if (_pageBrush is not null)
        {
            // 图页：先把那张图铺满整格。它已经是 Stretch=Uniform 的 ImageBrush，
            // 所以横竖比与留白由刷子自己管，这里不重复算一次。
            context.DrawRectangle(_pageBrush, null, new Rect(size));
        }
        else
        {
            context.DrawRectangle(new SolidColorBrush(_pageBackground), null, new Rect(size));
        }

        var bounds = ContentBounds();

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

    /// <summary>
    /// 笔迹该按哪一块来对位。
    /// <para>
    /// 图页按图本身的尺寸（<see cref="ContentSize"/>，从原点起），白板按墨迹自己的包围盒 ——
    /// 那边没有"页"，用户写到哪儿就以哪儿为准。
    /// </para>
    /// </summary>
    private Rect2D ContentBounds()
    {
        if (_contentSize is { Width: > 0, Height: > 0 } content)
        {
            return new Rect2D(0, 0, content.Width, content.Height);
        }

        var bounds = _document.GetBounds();
        return bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0
            ? new Rect2D(0, 0, 16, 9)
            : bounds;
    }

    private static Point Map(double x, double y, Rect2D bounds, double scale, double offsetX, double offsetY) =>
        new(x * scale + offsetX, y * scale + offsetY);
}
