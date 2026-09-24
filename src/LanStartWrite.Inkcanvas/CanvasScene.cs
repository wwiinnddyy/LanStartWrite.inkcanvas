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
/// 现在只有屏幕批注一个场景。再加一个场景 = 往这里加一个成员 + 在设置页给它一节 ——
/// 数据模型不用动（<see cref="CanvasSceneSettings"/> 是按场景存的）。
/// </para>
/// </summary>
internal enum CanvasScene
{
    /// <summary>屏幕批注：全屏透明画布盖在当前桌面上，边看边写。</summary>
    ScreenAnnotation = 0,
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
    /// </summary>
    public bool Freeze { get; init; }
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
