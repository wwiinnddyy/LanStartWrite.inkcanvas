namespace LanStartWrite.Inkcanvas;

internal sealed class WhiteboardPage
{
    internal WhiteboardPage(CanvasSurface surface)
    {
        Id = Guid.NewGuid();
        Surface = surface;
    }

    internal Guid Id { get; }

    internal CanvasSurface Surface { get; }
}
