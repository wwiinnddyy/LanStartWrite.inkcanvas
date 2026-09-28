namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// <b>用哪一套方式</b>跟正在放映的幻灯片联动。
/// </summary>
/// <remarks>
/// <para>
/// 这三档<b>不是三个实现细节，是三套不同的东西在哪一侧</b>：
/// <list type="bullet">
/// <item><see cref="BuiltIn"/> —— <b>内置</b>。本进程自己用 COM 连上宿主（PowerPoint / WPS），
/// 读"第几页 / 一共几页 / 哪一份文件"。<b>什么都不用装</b>，代价是要在本进程里处理 COM。</item>
/// <item><see cref="OfficeLink"/> / <see cref="PptMo"/> —— <b>外置插件</b>。
/// 由另一套东西负责与宿主说话，本应用只收结果 —— 换掉那一套不影响本应用。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么要枚举而不是一个 bool</b>：三档的实现位置不同、能力不同、失败方式也不同，
/// 而用户要能换。用 bool（"用内置的？"）表达不了"第三档"，
/// 而把三档硬编成三个并列分支在别处就会出现"漏了一处"——
/// 症状是"下拉里选了 X，而某处仍按 Y 处理"，且不报错。
/// </para>
/// <para>
/// <b>现在只有 <see cref="BuiltIn"/> 是实现了的。</b>另两档先占住位置，
/// 它们在设置页里各自带自己的设置项（见 <c>SlideShowLinkCatalog</c>），
/// 而"选了但用不了"要说人话 —— 参见 <see cref="SlideShowAvailability"/>。
/// </para>
/// </remarks>
internal enum SlideShowLinkMode
{
    /// <summary>内置联动：本进程用 COM 连宿主（PowerPoint / WPS 都行）。</summary>
    BuiltIn = 0,

    /// <summary>外置插件：OfficeLink。</summary>
    OfficeLink = 1,

    /// <summary>外置插件：pptmo。</summary>
    PptMo = 2,
}

/// <summary>
/// 联动"现在能不能用"的结论。<b>不是一个 bool</b>。
/// </summary>
/// <remarks>
/// 为什么分四档：和展台那一族同一条理由（见 <c>CameraAvailability</c>）。
/// "没装 PowerPoint"、"装了但没在放映"、"这一档还没接上" 是三件不同的事，
/// 而它们给用户的指引完全不同。压成一个 bool 之后，
/// 三种情况都会显示同一句"不可用" —— 而那句话对三种情况都不具体。
/// </remarks>
internal enum SlideShowAvailability
{
    /// <summary>还没问过。</summary>
    Unknown = 0,

    /// <summary>能用：连上了宿主，正在放映。</summary>
    Ready,

    /// <summary>宿主在、COM 也通，但<b>此刻没在放映</b>。</summary>
    NotPresenting,

    /// <summary><b>这一档还没接上</b>（外置插件那两档眼下都属这一类）。</summary>
    NotImplemented,

    /// <summary>连不上宿主：没装、或 COM 通道被挡。</summary>
    HostUnreachable,
}

/// <summary>一次联动探测的结论，带一句能直接给用户看的话。</summary>
/// <param name="Availability">四选一。</param>
/// <param name="Detail">人话原因。<b>不写技术名词</b>：用户看到的是"没找到 PowerPoint"，不是"REGDB_E_CLASSNOTREG"。</param>
/// <param name="SlideCount">这份演示文稿一共几页。<b>0</b> = 不知道。</param>
/// <param name="CurrentSlide">当前第几页，<b>1 基</b>。0 = 还没进放映。</param>
/// <param name="DeckId">接的是哪一份（<b>空串</b> = 还没接上）。墨迹按它关联，所以它不是可有可无的。</param>
internal readonly record struct SlideShowLinkReport(
    SlideShowAvailability Availability,
    string Detail,
    int SlideCount,
    int CurrentSlide,
    string DeckId)
{
    /// <summary>能不能真的拿到页。**只有 <see cref="SlideShowAvailability.Ready"/> 才是真能。</summary>
    internal bool IsLive => Availability == SlideShowAvailability.Ready;
}
