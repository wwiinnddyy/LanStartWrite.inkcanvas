namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 一页：<b>一块墨迹面 + 一张缩略图</b>。
/// <para>
/// 白板与图片批注的页在数据上只差一样东西 —— 底下那块是什么（纯色 / 一张图）。
/// 而那块东西<b>不是这一页自己的字段</b>：白板的底色来自设置、整块换一次；
/// 图片的图来自那个文件、每页各不同，但换不换也由这一页说了算。
/// 所以这里只留"每页都要有"的那两份，把"底下是什么"交给 <see cref="PagedCanvasWindow"/> 按场景统一铺。
/// </para>
/// <para>
/// 为什么不把底图也塞进这个基类当字段：那样白板就得为一张永远不存在的图留一个 null，
/// 而每页记住自己是哪张图又与"换图 = 换这一页的底"这件事重复。两份真相。
/// </para>
/// <para>
/// 它是 <b>public</b> 的，而 <see cref="CanvasSurface"/> 与 <see cref="WhiteboardThumbnail"/> 是 internal：
/// 因为 <see cref="PagedCanvasWindow"/> 是 public 基类，它的 protected 面上一旦出现 internal 类型，
/// 派生类就写不出那份覆写 —— 那是 CS0050/0051/0053 一串可访问性错误。
/// 所以只有"页这个概念"公开，两页里那两份具体东西仍不外露。
/// </para>
/// </summary>
public abstract class CanvasPage
{
    /// <summary>
    /// <b>internal 而不是 protected</b>：形参类型 <see cref="CanvasSurface"/> 是 internal，
    /// 用 protected 会撞 CS0051。而派生类（白板页、图片页）都在本程序集内，internal 足够。
    /// </summary>
    internal CanvasPage(CanvasSurface surface)
    {
        Surface = surface;
        Thumbnail = new WhiteboardThumbnail(surface.Document);
    }

    /// <summary>这块墨迹面。工具栏要写的一切（模式、颜色、粗细、擦法、撤销）都从它走。</summary>
    internal CanvasSurface Surface { get; }

    internal WhiteboardThumbnail Thumbnail { get; }
}
