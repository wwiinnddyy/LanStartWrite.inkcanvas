using System.Globalization;
using Jalium.UI;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// 放映批注的<b>幻灯片缩略图来源</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>它就是那道缝。</b>宿主（PowerPoint / WPS / ONLYOFFICE）那边千差万别 ——
/// VSTO 送 COM 对象、WPS 送 JSAPI 事件、ONLYOFFICE 送插件消息 ——
/// 但它们对本应用只回答一件事：<b>第 N 张幻灯片长什么样</b>。所以接口只留这一个方法，
/// 宿主那侧的差别全部关在实现里。
/// </para>
/// <para>
/// <b>返回 <see cref="FrameworkElement"/> 而不是位图</b>：缩略图那几格要画的是"卡片 + 图"，
/// 而卡片（边框、选中态、页码角标）是宿主无关的。让来源只交出"图"那一半，
/// 卡片样式就不会跟着换宿主而变 —— 换宿主时改的是这个文件，不是左下角那个控制器。
/// 真来源交出一张 <see cref="Image"/> 装宿主给的位图，合成来源交出一个自绘占位。
/// </para>
/// <para>
/// <b>刻意不要"翻页事件"在这个接口上</b>：翻页由放映驱动，而"谁翻的"有三层
/// （放映者按键 / 远程控制 / 用户点缩略图跳页），收进一个事件会立刻遇到
/// "这三者冲突时听谁的"。那是真来源接进来时要解决的取舍，不是这个契约现在要解决的问题。
/// </para>
/// <para>
/// <b>为什么值得单独一个接口</b>：本机没有 PowerPoint 放映环境可测，而左下角控制器、
/// 页码、选中态、翻页接线全都要能<b>在验收里跑通</b>。理由与 <c>IDocumentCameraFrames</c> 一字不差：
/// 没有接口就只能对着测不了的东西写代码。
/// </para>
/// </remarks>
internal interface ISlideThumbnailSource
{
    /// <summary>一共几张。<b>0</b> = 还没连上任何放映（不是"有一份零页的演示文稿"）。</summary>
    int SlideCount { get; }

    /// <summary>接的是哪一份演示文稿。<b>空串</b> = 还没连上。墨迹按它关联，所以它不是可有可无的。</summary>
    string DeckId { get; }

    /// <summary>
    /// 第 <paramref name="index"/> 张（<b>0 基</b>）的缩略图。
    /// </summary>
    /// <remarks>
    /// <b>允许返回 null，且那必须是合法状态</b>：幻灯片还没被宿主渲染出来很常见
    /// （刚打开、第一页还没画完）。调用方画一个"还没准备好"的占位，<b>不重试</b> ——
    /// 重试要么变成忙等，要么把"某一页没缩略图"升级成整个控制器打不开。
    /// </remarks>
    FrameworkElement? Thumbnail(int index);
}

/// <summary>
/// <b>模拟</b>放映来源：生成带页码的占位幻灯片。
/// </summary>
/// <remarks>
/// 它存在的唯一理由是<b>让样式能被验收</b>（见 <see cref="ISlideThumbnailSource"/> 那段）。
/// 设置页「放映管理 › 预览」走的就是这一份 —— 所以那个按钮验的是
/// <b>与真放映完全同一条路</b>，只有缩略图与页数是假的。
/// </remarks>
internal sealed class SyntheticSlideThumbnails : ISlideThumbnailSource
{
    /// <summary>一张幻灯片的<b>固定逻辑尺寸</b>（16:9，DIP）。</summary>
    /// <remarks>
    /// <b>必须是固定的，不能跟着窗口或投影仪分辨率走</b>：墨迹坐标钉在这张逻辑页上，
    /// 而它要盖在<b>正在放映</b>的那张幻灯片上。跟着屏幕尺寸变的话，换一台机器墨迹就跑位了。
    /// 真实放映时底下那张幻灯片也是这个比例（PowerPoint 默认 16:9），两边才对得上。
    /// </remarks>
    internal const double SlideWidth = 1280;

    internal const double SlideHeight = 720;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(0xFF, 0x2D, 0x7D, 0xD9),
        Color.FromArgb(0xFF, 0x2E, 0x9E, 0x6B),
        Color.FromArgb(0xFF, 0xC0, 0x53, 0x3C),
        Color.FromArgb(0xFF, 0x7A, 0x5A, 0xC7),
        Color.FromArgb(0xFF, 0xB8, 0x86, 0x1B),
        Color.FromArgb(0xFF, 0x1F, 0x8A, 0x8A),
    ];

    internal SyntheticSlideThumbnails(int slideCount = 12) => SlideCount = slideCount;

    public int SlideCount { get; }

    /// <summary>模拟放映没有真的 deck，给一个固定标识而不是空串 ——
    /// 空串是"还没连上"的语义，而这一份明明是连上的（它就是预览本身）。</summary>
    public string DeckId => "preview://模拟放映";

    public FrameworkElement? Thumbnail(int index) =>
        index < 0 || index >= SlideCount ? null : new SlidePlaceholder(Palette[index % Palette.Length], index + 1);
}

/// <summary>一张占位幻灯片：浅底 + 顶部色带 + 两行"标题线" + 页码。</summary>
/// <remarks>
/// <b>所有尺寸都按 <see cref="RenderSize"/> 的比例算，没有一个是写死的字号</b>：
/// 缩略图格只有一百六七十 DIP 宽，而同一份元素也要能放大到整屏当放映底图用，
/// 写死字号的结果是"缩略图里字大得撑破格子"或"全屏时字小得看不见"。
/// </remarks>
internal sealed class SlidePlaceholder : FrameworkElement
{
    private readonly Color _accent;
    private readonly int _number;

    internal SlidePlaceholder(Color accent, int number)
    {
        _accent = accent;
        _number = number;
    }

    protected override void OnRender(DrawingContext context)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        context.DrawRectangle(Brushes.White, null, new Rect(size));
        context.DrawRectangle(new SolidColorBrush(_accent), null,
            new Rect(0, 0, size.Width, Math.Max(2, size.Height * 0.10)));

        // 两行"标题线"，让占位图看起来像有内容的幻灯片，而不是一块纯色。
        var line = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        var lineHeight = Math.Max(1, size.Height * 0.045);
        context.DrawRectangle(line, null, new Rect(size.Width * 0.10, size.Height * 0.34, size.Width * 0.52, lineHeight));
        context.DrawRectangle(line, null, new Rect(size.Width * 0.10, size.Height * 0.46, size.Width * 0.34, lineHeight));

        var text = new FormattedText(
            _number.ToString(CultureInfo.CurrentCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            Math.Max(6, size.Height * 0.26),
            new SolidColorBrush(Color.FromArgb(0xCC, 0x33, 0x33, 0x33)),
            pixelsPerDip: 96);
        context.DrawText(text, new Point((size.Width - text.Width) / 2, size.Height * 0.58));
    }
}
