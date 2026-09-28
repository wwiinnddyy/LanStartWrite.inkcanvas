using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// 放映的一页 = <b>一张幻灯片</b>。
/// </summary>
/// <remarks>
/// <para>
/// 它与 <see cref="WhiteboardPage"/> / <see cref="ImagePage"/> 在数据上只差一样东西：
/// 这一页对应的幻灯片缩略图（<see cref="Slide"/>）。而那一项<b>不是本类型自己画的</b>，
/// 是从 <see cref="ISlideThumbnailSource"/> 要来的 —— 所以真 PPT 来源接进来时，
/// <b>这个类型一行都不用改</b>。
/// </para>
/// <para>
/// <b>为什么每一页还要记住自己的缩略图</b>：换页时控制器要同时刷"当前页高亮"与
/// "那一格显示第几张"。缩略图是异步/可能失败的（见 <see cref="ISlideThumbnailSource.Thumbnail"/>），
/// 所以它属于"这一页当时拿到了什么"，而不是"第几页"能算出来的东西 ——
/// 少了这一项，第 5 页没缩略图时会去拿第 0 页的图顶上，而那在界面上是"两张页长得一样"。
/// </para>
/// </remarks>
internal sealed class SlideShowPage : CanvasPage
{
    /// <summary>这一页的幻灯片画面。宿主没渲染出来时是 null，控制器画"还没准备好"。</summary>
    internal FrameworkElement? Slide { get; set; }

    /// <summary>取这一页的幻灯片位图，<b>没有时给 null</b>。</summary>
    /// <remarks>
    /// 只有 <see cref="Image"/> 那种来源才带位图（真 PPT 来源走这条）；合成来源给的是
    /// 自绘占位元素，它没有位图可取 —— 那时返回 null，调用方改用占位底色，
    /// <b>而不是去造一张</b>。造一张就要为占位再养一份缓冲，而它在预览模式下每页都会被要一次。
    /// </remarks>
    internal ImageSource? SlideImage => (Slide as Image)?.Source;

    /// <summary>这一页是第几张（<b>1 基</b>，给人看的）。</summary>
    internal int SlideNumber { get; init; }

    internal SlideShowPage(CanvasSurface surface, int slideNumber)
        : base(surface)
    {
        SlideNumber = slideNumber;
    }
}
