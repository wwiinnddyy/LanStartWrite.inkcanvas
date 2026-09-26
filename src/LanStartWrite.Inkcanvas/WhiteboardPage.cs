namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 白板的一页：底下是设置里选的那档纯色，没有自己的图。
/// <para>
/// 这一类现在只剩"构造方式不同"（<see cref="CanvasPage"/> 已经把两份东西都备好了），
/// 但<b>留着它</b>而不是直接用 <see cref="CanvasPage"/>：分页列表里要能一眼看出
/// "这一页是白板页还是图片页"，而 <c>ImagePage</c> 还要挂自己的图与朝向。
/// 两种页混在一份列表里时，靠类型分辨比靠一堆 <c>is</c> 判断可靠。
/// </para>
/// </summary>
public sealed class WhiteboardPage : CanvasPage
{
    internal WhiteboardPage(CanvasSurface surface)
        : base(surface)
    {
    }
}
