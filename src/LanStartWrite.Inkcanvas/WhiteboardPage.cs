namespace LanStartWrite.Inkcanvas;

internal sealed class WhiteboardPage
{
    internal WhiteboardPage(CanvasSurface surface)
    {
        Id = Guid.NewGuid();
        Surface = surface;
        Thumbnail = new WhiteboardThumbnail(surface.Document);
    }

    internal Guid Id { get; }

    internal CanvasSurface Surface { get; }

    internal WhiteboardThumbnail Thumbnail { get; }
}
