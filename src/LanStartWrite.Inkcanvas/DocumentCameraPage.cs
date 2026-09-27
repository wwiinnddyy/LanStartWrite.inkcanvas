using System.Windows;
using Jalium.UI;
using Jalium.UI.Media.Imaging;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 视频展台那一"页"。<b>永远只有一页</b>，而且它的尺寸与摄像头分辨率<b>无关</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>尺寸固定成横向 A4</b>（1122×793 DIP）而不是"当前帧的像素大小"，是这块画布
/// 最重要的一条设计决定：
/// </para>
/// <list type="number">
/// <item>设备重新协商到别的分辨率时，<b>墨迹坐标一动不动</b>。
/// 若把世界设成视频像素大小，用户会在设备换档时看见自己的字"跑到别处去了" ——
/// 而他并没有碰任何东西。</item>
/// <item>"存成页"之后，冻结帧与墨迹的坐标<b>天然与图片画布那一页重合</b>，
/// 于是搬运笔迹不需要任何换算（那一步是这类功能最容易出错的地方）。</item>
/// </list>
/// <para>
/// 代价是<b>画面会有轻微变形</b>：A4 横向是 1.41:1，而摄像头多为 4:3 或 16:9。
/// 这里用 <c>Stretch=Fill</c> 而不是 <c>Uniform</c>，是刻意的 ——
/// <c>Uniform</c> 会留边，而"纸上写着字、字却被挤在中间"比轻微变形更让人分心
/// （这与图片画布那条"横向不齐按最宽的居中、不拉伸"的判断同源：变形是刺眼的对齐问题，
/// 不是不可容忍的）。
/// </para>
/// </remarks>
internal sealed class DocumentCameraPage : CanvasPage
{
    /// <summary>横向 A4，单位 DIP。</summary>
    internal const double LogicalWidth = 1122;

    /// <summary>横向 A4，单位 DIP。</summary>
    internal const double LogicalHeight = 793;

    /// <summary>这一页的尺寸 —— <b>恒定</b>，与任何设备、任何分辨率无关。</summary>
    internal Size PageSize { get; } = new(LogicalWidth, LogicalHeight);

    /// <summary>
    /// 当前正在显示的那一帧。<b>这一页不拥有它</b>：帧源在下一帧到达时会释放上一张，
    /// 所以要留住（冻结时、"存成页"时）必须拷一份，见 <see cref="Snapshot"/>。
    /// </summary>
    internal BitmapImage? LiveFrame { get; set; }

    /// <summary>已经拷好、可以长期持有的那一帧（冻结那一刻留下的）。</summary>
    internal BitmapImage? FrozenFrame { get; set; }

    /// <summary>
    /// 拷一份当前画面。<b>冻结留底与"存成页"都必须先拷</b>：
    /// 直接留引用会在下一帧到达时被 Dispose 掉，于是"冻结的那张"变成一张已释放的图 ——
    /// 而症状是"冻结之后过一会儿画面变白"，很难往"引用被释放"上想。
    /// </summary>
    /// <remarks>
    /// 走 <c>CopyPixels</c> 而不是直接摸 <c>RawPixelData</c>：后者是内部的原始缓冲，
    /// 它<b>可能不是当前显示的那一份</b>（解码器走过的路径会另存），
    /// 而这里要的是"用户在屏幕上看到的那张"。stride 用 <c>PixelStride</c>，
    /// 因为位图缓冲的行距不保证等于 <c>width × 4</c>。
    /// </remarks>
    internal BitmapImage? Snapshot()
    {
        var source = FrozenFrame ?? LiveFrame;
        if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0) return null;

        var stride = source.PixelStride;
        var buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), buffer, stride, 0);
        return BitmapImage.FromPixels(buffer, source.PixelWidth, source.PixelHeight, stride);
    }

    internal DocumentCameraPage(CanvasSurface surface) : base(surface)
    {
    }
}
