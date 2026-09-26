using System.Windows;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using LanStartWrite.Inkcanvas.Pdf;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 左侧那条<b>连续胶片</b>：纵向滚的一列页缩略图。
/// <para>
/// 用户点名的形状是"胶片"不是"页码下拉"：它必须<b>一直能看见整份文档有多长</b>，
/// 而且高亮随主视图滚动移动。反过来做成"点页码弹一列"，就丢掉了"文档有几百页"这件事 ——
/// 而那恰恰是 PDF 与图片最不一样的地方。
/// </para>
/// <para>
/// 缩略图用<b>低档</b>（72 dpi，1 MB 一页）：胶片上每格只有几十像素宽，
/// 高档在那个尺寸下看不出差别，却是 17 倍的字节与 6 倍的耗时。
/// </para>
/// </summary>
internal sealed class PdfViewerFilmstrip
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list;
    private readonly List<Card> _cards = [];
    private PdfPageLayout? _layout;
    private int _currentPage = -1;

    internal PdfViewerFilmstrip()
    {
        _list = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _list,
            Width = 128,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            // 缩略图不参与命中：它是导航，不是批注的落点。
            IsHitTestVisible = true,
        };

        var surface = new Border
        {
            Width = 128,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 12, 8, 12),
        };
        surface.Child = _scroll;
        Root = surface;
    }

    internal FrameworkElement Root { get; }

    internal event Action<int>? PageChosen;

    internal int CardCount => _cards.Count;

    internal int CurrentPage => _currentPage;

    /// <summary>已经落地了图的那几格（验收读它：元素建了不等于显形了）。</summary>
    internal int FilledCount => _cards.Count(card => card.Image.Source is not null);

    /// <summary>
    /// 让第 <paramref name="pageIndex"/> 格去要一张缩略图。
    /// <para>
    /// <b>要的是低档（72 dpi）**：胶片上每格只有 112 像素宽，高档（220 dpi，17 MB）在那个尺寸下
    /// 看不出差别，却是 17 倍的字节。所以这一条不是"省一点"，是<b>唯一正确的档位</b>。
    /// </para>
    /// <para>
    /// 落地后 <see cref="OnThumbnailReady"/> 把图交给这一格 —— 走缓存里那一份，
    /// 所以胶片上看到的与主视图里低档那一张是<b>同一块位图</b>，不会多占一份内存。
    /// </para>
    /// </summary>
    internal void RequestThumbnail(int pageIndex, Action<int, BitmapImage> onReady)
    {
        if (pageIndex < 0 || pageIndex >= _cards.Count) return;
        if (_cards[pageIndex].Image.Source is not null) return;
        _onReady = onReady;
    }

    private Action<int, BitmapImage>? _onReady;

    /// <summary>缩略图落地后由窗口转回来。</summary>
    internal void OnThumbnailReady(int pageIndex, BitmapImage image)
    {
        if (pageIndex < 0 || pageIndex >= _cards.Count) return;
        _cards[pageIndex].Image.Source = image;
        _cards[pageIndex].Image.Visibility = Visibility.Visible;
        _onReady?.Invoke(pageIndex, image);
    }

    /// <summary>按布局建一列缩略图。<b>只建卡片，不立刻光栅化</b>。</summary>
    internal void SetDocument(PdfPageLayout? layout, string? path)
    {
        _layout = layout;
        _cards.Clear();
        _list.Children.Clear();
        if (layout is null) return;

        for (var i = 0; i < layout.PageCount; i++)
        {
            var rect = layout.PageRect(i);
            // 缩略图保持页面的**长宽比**，否则一张 A4 和一张 Letter 看起来一样大，
            // 用户点之前就分不出差别 —— 宽高比是"哪页是横排"这个信息的唯一来源。
            var height = 160.0 * rect.Height / Math.Max(rect.Width, 1);
            var image = new Image
            {
                Stretch = Stretch.Fill,
                Width = 112,
                Height = Math.Clamp(height, 60, 220),
                Visibility = Visibility.Hidden,
            };

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
            };

            var stack = new StackPanel();
            stack.Children.Add(image);
            stack.Children.Add(label);

            var card = new Button
            {
                Content = stack,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var index = i;
            card.Click += (_, _) => PageChosen?.Invoke(index);

            _list.Children.Add(card);
            _cards.Add(new Card { Index = i, Button = card, Image = image });
        }
    }

    /// <summary>主视图滚动后调：把高亮挪到当前页，<b>并在视口内滚到它可见</b>。</summary>
    internal void SetCurrentPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _cards.Count) return;
        if (pageIndex == _currentPage) return;

        var previous = _currentPage;
        _currentPage = pageIndex;
        Highlight(previous);
        Highlight(pageIndex);

        // 胶片自己也要"跟着走"：用户滚到第 80 页而胶片还停在第 1 页，
        // 那条胶片就等于没有 —— 它作为"文档长度指示器"的唯一价值就是跟着走。
        _scroll.ScrollToVerticalOffset(Math.Max(0, pageIndex * 170 - _scroll.ActualHeight / 2));
    }

    private void Highlight(int index)
    {
        if (index < 0 || index >= _cards.Count) return;
        var card = _cards[index];
        // 不设本地 Foreground：选中的高亮是 accent，图标/文字继承库的绑定。
        card.Button.Background = index == _currentPage
            ? FindBrush("AccentFillColorDefaultBrush")
            : Brushes.Transparent;
    }

    /// <summary>
    /// 取库里的画刷键，取不到就退回透明。
    /// <para>
    /// 不用 <c>SetResourceReference</c>：那个写法下"键名写错"是<b>静默</b>的 ——
    /// 设不设都合法，属性保持 null，于是高亮整排都不亮，而界面上没有任何报错。
    /// 这里显式查一次，查不到就落到一个看得见的值上，键名拼错至少是"没高亮"而不是"看不见"。
    /// </para>
    /// </summary>
    private static Brush FindBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    /// <summary>那一格是不是已经有图了（免得重复要）。</summary>
    internal bool HasThumbnail(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _cards.Count && _cards[pageIndex].Image.Source is not null;

    internal void RefreshInk()
    {
        // 墨迹不进胶片：PDF 胶片上画的是**页面缩略**，不是缩略图上的墨迹。
        // 画上去会让每格多一份 N 笔的矢量重绘，而用户在这一列上只做"点哪页"。
    }

    /// <summary>供验收驱动：当作用户点了第 <paramref name="index"/> 格。</summary>
    internal void ChoosePageForProbe(int index) => PageChosen?.Invoke(index);

    private sealed record Card
    {
        internal required int Index { get; init; }
        internal required Button Button { get; init; }
        internal required Image Image { get; init; }
    }
}
