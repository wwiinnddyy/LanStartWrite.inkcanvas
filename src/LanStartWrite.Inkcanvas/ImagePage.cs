using Jalium.UI;
using Jalium.UI.Media.Imaging;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 图片批注的一页：底下是<b>一个打开的图片文件</b>。
/// <para>
/// 与白板页的差别全在这一页自己身上：那张图是哪一张、现在朝哪边。这两样都是"这一页独有的"，
/// 所以放在这里而不是散在窗口的字段里 —— 多页时窗口要能同时记住每页各自的图与朝向，
/// 而它们互不相干。
/// </para>
/// <para>
/// <b>朝向用 90° 的整数步记，不记角度</b>：引擎的视口只有平移与缩放（见 <c>Dusk/docs/07-infinite-canvas.md</c>），
/// 图那一侧要跟着转只能是"把这一页的尺寸对调"，而任意角度会让页面外框变成非矩形，
/// 于是落笔边界、橡皮半径换算、缩略图裁切全都要跟着处理。<b>整数步让页面始终是一个矩形</b>，
/// 代价是只能转 90/180/270。
/// </para>
/// </summary>
public sealed class ImagePage : CanvasPage
{
    internal ImagePage(CanvasSurface surface, string path, BitmapImage? image)
        : base(surface)
    {
        Path = path;
        Image = image;
        PageSize = image is null ? default : new Size(image.PixelWidth, image.PixelHeight);
    }

    /// <summary>这个文件是哪一张（只用于窗口标题与缩略图上的名字，不参与渲染）。</summary>
    public string Path { get; }

    /// <summary>
    /// 那一张图。<b>始终是原朝向</b>，旋转不烘进位图 —— 烘进去就得复制一份几千万像素的缓冲。
    /// <para>
    /// <b>类型必须是 <see cref="BitmapImage"/>，不能是 <c>BitmapFrame</c></b>：
    /// <c>Image</c> 的解码与 GPU 上传全建在它上面（<c>Image.RequestBitmapDecode</c> 第一句就是
    /// <c>if (Source is not BitmapImage bitmap) return;</c>，<c>OnSourceChanged</c> 的加载与失败分支
    /// 也都是 <c>BitmapImage</c> 专用），而 <c>BitmapFrame.NativeHandle</c> 恒为 0。
    /// 喂错类型的后果是<b>什么都不画</b>而不是报错 —— 症状是"标题栏说已打开，窗口一片黑"。
    /// </para>
    /// <para><b>可空</b>：窗口一进来时还没有任何页，用户选文件之前这一页底下什么都不该有。</para>
    /// </summary>
    public BitmapImage? Image { get; }

    /// <summary>已经转过几个 90°（0..3，取模 4）。</summary>
    public int QuarterTurns { get; private set; }

    /// <summary>这一页<b>当前朝向下的尺寸</b>（世界单位 = 像素）。奇数步就与原图宽高对调。</summary>
    public Size PageSize { get; private set; }

    /// <summary>文件名（不含路径），给缩略图那一行与窗口标题用。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// 再转 90°（<paramref name="direction"/>：+1 顺时针，-1 逆时针），并更新这一页的尺寸。
    /// <para><b>只算尺寸，不碰位图</b>：图怎么画是窗口的事（见 <c>ImageViewerWindow</c>），
    /// 这里只保证"尺寸"与"朝向"这两份不会各说各话 —— 尺寸是旋转之后要拿来对位笔迹的，
    /// 若它与图实际画出来的大小差 1 像素，笔迹就会整体偏出去。</para>
    /// </summary>
    public void TurnQuarter(int direction)
    {
        QuarterTurns = ((QuarterTurns + direction) % 4 + 4) % 4;
        var width = PageSize.Width;
        var height = PageSize.Height;
        PageSize = QuarterTurns is 1 or 3 ? new Size(height, width) : new Size(width, height);
    }
}
