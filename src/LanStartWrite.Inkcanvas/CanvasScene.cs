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

    /// <summary>
    /// 视频展台（实物展台）：<b>活着的一个面</b>，不是一份文件
    /// <para>
    /// 前三块画布底下铺的东西都是<b>静态</b>的 —— 一张图、一份文档、一页笔记，
    /// 铺上去之后世界原点就不再动。而展台底下是<b>摄像头</b>：
    /// 每秒二三十帧新像素推进来，而墨迹在它上面不动。
    /// </para>
    /// <para>
    /// <b>它不是第五种"页"。</b> 页是可以翻的，而这里只有一面；
    /// 所以它固定为<b>一张逻辑页面</b>（横向 A4），视频按 Stretch=Fill 铺上去 ——
    /// 这样换分辨率、换设备、设备掉线重连时，<b>墨迹坐标一动不动</b>。
    /// 若把世界设成视频像素大小，用户在设备重新协商到 720p 时会看见自己的字"跑到别处去了"。
    /// </para>
    /// <para>
    /// 与图片画布的另一处不同：那一块"底"是一张 <c>BitmapImage</c>，可以整个搬走；
    /// 这里是<b>每帧换一张</b>，于是"上一张必须释放"成了硬要求
    /// （<c>BitmapImage</c> 是 <c>IDisposable</c>，30fps 下一帧一换而不释放
    /// 就是每秒 30 次 GPU 资源堆积）。
    /// </para>
    /// </summary>
    DocumentCamera = 4,

    /// <summary>
    /// 第六块画布：<b>PPT 放映批注</b>。
    /// <para>
    /// 它和前四块的分页模型<b>同构</b>（一页一世界、翻页换面），所以它也走
    /// <see cref="PagedCanvasWindow"/>：每一页幻灯片一块墨迹面，翻页换挂 —— 而<b>不是</b> PDF 那种
    /// "一整份一个世界、翻页挪视口"。理由与 PDF 那条相反：放映时底下那张幻灯片是
    /// <b>一张独立的整页画面</b>，它有自己的比例与边界，墨迹必须钉在它上面。
    /// </para>
    /// <para>
    /// <b>与其它块最不同的一点是"底下是什么"</b>：前几块底下是静态的（纯色 / 一张图 / 一份文档），
    /// 而放映时底下是 <b>正在被别的程序全屏播放的幻灯片</b> —— 我们的窗口是盖在它上面的
    /// <b>透明</b>全屏层，<b>不自己铺底</b>。
    /// <para>
    /// 所以"底下垫什么"这一件事在这里是<b>空的</b>，而这恰恰是它与图片/展台的分界：
    /// 那两边要垫，<b>这边垫了就等于把别人的放映盖住</b>。
    /// </para>
    /// <para>
    /// 这一阶段先跑的是<b>模拟放映</b>（设置页「放映管理 › 预览」）：底下垫一张占位图模拟幻灯片，
    /// 用来把样式（左下角页面控制器、沉浸式工具栏）真做出来。真 PPT 来源从同一道缝接进来。
    /// </para>
    /// </summary>
    Slideshow = 5,
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

    /// <summary>
    /// 视频展台<b>水平镜像</b>。
    /// <para>
    /// 实物展台是架在纸<b>上方</b>往下拍的，所以用户看到的是左右反的 ——
    /// 于是他写在纸右边的话在自己眼里是反的，几分钟就能把一个习惯矫正的书写者逼疯。
    /// 这一项就是那面镜子。
    /// </para>
    /// <para>
    /// <b>只在展台这一块有意义</b>，所以它是按场景存的而不是全局的：
    /// 同一个开关对白板与图片毫无意义，共用一个值就等于给无关的场景加了一个旋钮。
    /// </para>
    /// <para><b>当场生效</b>：展台在屏时改它，下一帧就正过来（改的是铺在墨迹面下的那张图的变换）。</para>
    /// </summary>
    public bool CameraMirror { get; init; }
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
