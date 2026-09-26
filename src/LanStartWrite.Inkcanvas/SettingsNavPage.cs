namespace LanStartWrite.Inkcanvas;

internal enum SettingsNavPage
{
    Appearance,
    Ink,
    Canvas,

    /// <summary>
    /// 文件：打开图片的方式、上次目录、以及默认图片查看器。
    /// <para>它挨着「画布」而不是塞进「画布」那一页：它管的是<b>打开什么</b>与<b>怎么开</b>，
    /// 而「画布」管的是已经打开之后这块画布怎么表现（透不透、冻不冻、底什么色）。
    /// 两件事的时间点不同，混在一页里用户会以为改了就立刻影响眼前的画布。</para>
    /// </summary>
    File,

    Toolbar,
    Interaction,
    About,
}
