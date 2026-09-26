using System.Windows;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas.Pdf;

/// <summary>
/// 整份 PDF 在<b>世界坐标</b>里的排布：一份文档 = 一个世界 = 一次平移。
/// <para>
/// 这是 A 方案（单 <c>CanvasSurface</c> 装整份文档）的<b>地基</b>，也是它与
/// <see cref="PagedCanvasWindow"/> 唯一的结构差别：那边是 N 个世界、一次挂一个，
/// 所以"翻页"是换一个 surface 挂上去；这边<b>只有一个世界</b>，N 页是同一片纸上的
/// N 个矩形，于是"翻页"退化成"把视口挪到第 k 个矩形的顶边"——<b>不需要换任何东西</b>。
/// </para>
/// <para>
/// 单位是<b>点</b>（1/72 英寸），因为 PDF 本身就是点单位的：
/// 于是 100% 缩放下一个 Letter 页是 612 DIP ≈ 8.5 英寸，恰好是纸在屏幕上的真实大小，
/// 而"200%"就是真纸的两倍，不需要任何换算系数。
/// </para>
/// <para>纯计算，没有 UI 也没有引擎 —— 所以这份几何可以被十几行断言钉住。</para>
/// </summary>
internal sealed class PdfPageLayout
{
    /// <summary>
    /// 页与页之间留的空隙（点）。24 pt ≈ 3.2 mm，正好是两页之间那道纸边 ——
    /// 看得清这是两页，而不是一张画到一半的白纸。
    /// </summary>
    internal const double PageGap = 24;

    /// <summary>文档四周留白（点），免得第一页贴着窗口边、最后一页也贴着。</summary>
    internal const double Margin = 32;

    private readonly Rect[] _pages;

    internal PdfPageLayout(IReadOnlyList<(double WidthPt, double HeightPt)> pageSizes)
    {
        _pages = new Rect[pageSizes.Count];
        if (pageSizes.Count == 0) return;

        // 横向按最宽的那一页对齐：窄的那几页居中，看起来才像同一份文档
        // （全部按最宽的拉伸会让 A4 和 Letter 互相变形，而变形是 PDF 里最刺眼的错）。
        var contentWidth = 0.0;
        foreach (var (width, height) in pageSizes)
            if (width > contentWidth) contentWidth = width;

        var y = Margin;
        for (var i = 0; i < pageSizes.Count; i++)
        {
            var (width, height) = pageSizes[i];
            var x = Margin + (contentWidth - width) / 2;
            _pages[i] = new Rect(x, y, width, height);
            y += height + PageGap;
        }

        ContentWidth = contentWidth + Margin * 2;
        ContentHeight = y - PageGap + Margin;
    }

    /// <summary>页数。</summary>
    internal int PageCount => _pages.Length;

    /// <summary>整个文档的宽（点）。</summary>
    internal double ContentWidth { get; }

    /// <summary>整个文档的高（点）—— 纵向滚动的总行程。</summary>
    internal double ContentHeight { get; }

    /// <summary>第 <paramref name="index"/> 页在世界里占的那个矩形。</summary>
    internal Rect PageRect(int index) =>
        (uint)index < (uint)_pages.Length ? _pages[index] : Rect.Empty;

    /// <summary>第 <paramref name="index"/> 页顶边的 y —— 翻页时的落点。</summary>
    internal double PageTop(int index) => PageRect(index).Top;

    /// <summary>
    /// 世界点落在第几页；落在页与页之间的空隙里返回 −1。
    /// <para>
    /// <b>返回 −1 而不是就近那一页</b>：空隙是"两页之间"，落笔在那儿不该算到某一页头上。
    /// 而它返回 −1 的后果是"这一笔没有归属"（不进任何页的批注层），
    /// 这比偷偷归给下一页诚实 —— 那样用户会发现空隙里的笔迹跟着下一页跑。
    /// </para>
    /// </summary>
    internal int PageAt(Point world)
    {
        for (var i = 0; i < _pages.Length; i++)
            if (_pages[i].Contains(world)) return i;
        return -1;
    }

    /// <summary>
    /// 视口中心在第几页 —— <b>页码与缩略图胶片靠它跟随滚动</b>。
    /// <para>与 <see cref="PageAt"/> 的区别：这里问的是"用户正在看哪一页"，
    /// 所以<b>取最近的</b>而不是"命中才算"——站在两页中间时页码不能空着。</para>
    /// <para>
    /// 恰好平分时（正好在两页中心的连线上）<b>归前一页</b>（比较用严格小于，后来的赢不了）。
    /// 这不是随手写的：用户是"从上往下滚"过来的，卡在分界上时他还在上一页的末尾，
    /// 页码报下一页会让胶片先跳一步再回弹。
    /// </para>
    /// </summary>
    internal int NearestPageTo(double worldY)
    {
        if (_pages.Length == 0) return -1;

        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _pages.Length; i++)
        {
            var centre = _pages[i].Top + _pages[i].Height / 2;
            var distance = System.Math.Abs(centre - worldY);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// 把纵向滚轮/拖动<b>吸附</b>到页边界 —— 切页模式用，连续模式不用。
    /// <para>
    /// 吸附只在<b>离页边界很近</b>时才生效（<paramref name="snapDip"/>）：
    /// 一路拖到底再松手也吸到最近那页，听起来像切页模式，但用户明明在自由浏览 ——
    /// 而连续模式与切页模式的差别就在这里，所以判据必须是"用户是不是想停在一页上"。
    /// </para>
    /// </summary>
    internal double SnapScrollTo(double worldY, double snapDip = 40)
    {
        if (_pages.Length == 0) return 0;
        for (var i = 0; i < _pages.Length; i++)
        {
            var top = _pages[i].Top;
            if (System.Math.Abs(worldY - top) <= snapDip) return top;
        }

        return worldY;
    }
}
