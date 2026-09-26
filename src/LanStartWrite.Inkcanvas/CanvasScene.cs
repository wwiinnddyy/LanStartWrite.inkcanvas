namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 画布<b>场景</b>：一块画布是给什么用的。
/// <para>
/// 之所以把它单独列出来，是因为"画布"从来不是一个东西：屏幕批注要的是"盖在当前桌面之上"，
/// 白板要的是一块干净底、文档批注要的是贴在某一页上 —— 它们的设置项不一样，甚至默认值相反
/// （屏幕批注默认半透明透出桌面，白板不能透）。所以设置<b>按场景存</b>，
/// 每个场景一套自己的开关，改一个不会动另一个。
/// </para>
/// <para>
/// 两个场景<b>各有各的一套开关</b>，加一个场景 = 往这里加一个成员 + 在设置页给它一节 ——
/// 数据模型不用动（<see cref="CanvasSceneSettings"/> 是按场景存的）。
/// </para>
/// <para>
/// 注意这里说的是"这块画布该怎么表现"，不是"此刻哪块画布在眼前" —— 后者是运行时形态，
/// 在 <see cref="CanvasSceneState"/>，不落盘。
/// </para>
/// </summary>
public enum CanvasScene
{
    /// <summary>屏幕批注：全屏透明画布盖在当前桌面上，边看边写。</summary>
    ScreenAnnotation = 0,

    /// <summary>
    /// 白板：全屏<b>不透明</b>的一块底（颜色在 <see cref="CanvasSceneSettings.BackgroundArgb"/>），
    /// 「鼠标」这一档在这里的意思是"选择墨迹"，并且允许双指漫游。
    /// </summary>
    Whiteboard = 1,

    /// <summary>
    /// 图片批注：底下铺一张打开的图，图上写字。
    /// <para>
    /// 与白板共用同一套"选择 / 变换 / 漫游"手势，但底下那块不是纯色而是一张位图 ——
    /// 所以它多两件别处没有的事：<b>换朝向</b>（图与笔迹一起转，只走 90° 的整数步）与
    /// <b>停靠</b>（窗口模式下工具栏动画停在图片窗口下方，而不是浮在屏幕底部）。
    /// </para>
    /// <para>
    /// 一个文件一页，多个文件就是多页 —— 与白板的分页模型同构，所以那边那套缩略图导航
    /// 与 <c>InkViewport</c> 缩放漫游整条都直接复用。
    /// </para>
    /// </summary>
    ImageCanvas = 2,

    /// <summary>
    /// 第四块画布：PDF 批注。
    /// <para>
    /// 它是<b>唯一一个"整份文档一个世界"</b>的场景：白板与图片都是一页一个世界、
    /// 一次只挂一块墨迹面，而 PDF 是一块面、一份文档、N 页同一片纸上的 N 个矩形。
    /// 所以"翻页"在那边是换一块面挂上去，在这边只是挪一下视口。
    /// </para>
    /// <para>
    /// 底色与冻结那几项对 PDF <b>不适用</b>，而它们仍然挂在 <c>CanvasOptions</c> 上 ——
    /// 与其给它们加"仅对某些场景有效"的分支，不如留着：读档时那份数据照旧在，
    /// 回到白板立刻还是原来那个底色。
    /// </para>
    /// </summary>
    PdfCanvas = 3,
}

/// <summary>
/// 一个画布场景的设置。
/// <para>
/// 每个开关都配一句"它在什么时候起作用"，因为这几项都不是"点一下立刻变"的即时开关，
/// 而是"下次进入画布时按这个来" —— 写清楚触发时机比写清楚效果更重要。
/// </para>
/// </summary>
internal sealed record CanvasSceneSettings
{
    public CanvasScene Scene { get; init; } = CanvasScene.ScreenAnnotation;

    /// <summary>
    /// 穿透模式。<b>鼠标模式下</b>画布照旧盖在最上层，但鼠标 / 触摸直接落到下面的窗口上，
    /// 于是"批注留着不动、人继续操作电脑"成立。
    /// <para>
    /// 它改的是鼠标模式的行为：关掉时鼠标模式会把画布收起来，打开时改为留着但不接收输入。
    /// 书写与擦除不受影响 —— 那两种模式本来就要接住输入。
    /// </para>
    /// </summary>
    public bool PassThrough { get; init; }

    /// <summary>
    /// 冻结模式。<b>进入画布（书写 / 擦除）时</b>把当前屏幕截一张图，铺在画布最底下，
    /// 于是底下的画面不再变（视频、滚动页面、闪烁的进度条都停在那一刻），
    /// 而笔记写在它上面。
    /// <para>
    /// 截的是"这次进入画布之前"的那一屏：所以每次重新进入都会重截一张，不是一直用第一张。
    /// </para>
    /// <para>这一项只对屏幕批注有意义：白板本来就要盖住桌面，没有"透出去"与"冻住"这两种状态。</para>
    /// </summary>
    public bool Freeze { get; init; }

    /// <summary>
    /// 白板那一块底色（打包成整数的 ABGR 字节序，见 <see cref="Argb"/>）。
    /// <para>
    /// 存整数而不是 <c>Color</c>：框架类型的序列化格式由框架定，而这份格式要长期读写。
    /// 它是<b>按场景</b>的 —— 白板的干净底与批注的透出桌面是两件事，共用一个值就互相改。
    /// </para>
    /// <para>
    /// 与上面两个开关不同，这一项<b>当场生效</b>：白板在屏时改它，那块底立刻换颜色。
    /// 三档取值在 <see cref="CanvasBackgroundPalette"/>，都是浅底（深色底会把默认的黑色墨迹吃掉）。
    /// </para>
    /// </summary>
    public uint BackgroundArgb { get; init; } = CanvasBackgroundPalette.DefaultArgb;
}

/// <summary>
/// 各场景的画布设置，按场景标识取。
/// <para>
/// 包一层是为了<b>按值比较</b> —— 理由与 <see cref="TipPresetCollection"/> 一字不差：
/// record 的合成 <c>Equals</c> 对 <see cref="List{T}"/> 是按引用比的，
/// 直接放进去会让"存盘再读回来相等"那条断言在往返之后变红。
/// </para>
/// </summary>
internal sealed record CanvasSceneCollection
{
    /// <summary>可空理由同 <see cref="TipValueVector.Values"/>：它从文件里来。</summary>
    public List<CanvasSceneSettings>? Items { get; init; } = [];

    public bool Equals(CanvasSceneCollection? other)
    {
        if (other is null) return false;

        var mine = Items ?? [];
        var theirs = other.Items ?? [];
        if (mine.Count != theirs.Count) return false;
        for (var i = 0; i < mine.Count; i++)
            if (mine[i] != theirs[i]) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items ?? []) hash.Add(item);
        return hash.ToHashCode();
    }
}
