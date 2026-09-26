using System.Collections.Concurrent;
using System.Diagnostics;
using Jalium.UI.Media.Imaging;

namespace LanStartWrite.Inkcanvas.Pdf;

/// <summary>
/// PDF 页光栅的缓存：<b>LRU + 字节预算</b>，并且是<b>整个进程里唯一碰 PDFium 的地方</b>。
/// <para>
/// "唯一"是承重的：PDFium 的文档与页对象<b>都</b>不是线程安全的，
/// 而光栅化必须离开 UI 线程（不然一帧卡 25 ms）。两条合起来就要求
/// "从任何线程发起的请求"都得排队到这里来，所以锁在这一层、只在这里。
/// </para>
/// <para>
/// <b>键是 (页, 档)</b>而不是页：同一页的低档与高档要能<b>同时</b>在缓存里 ——
/// 移动时用低档画着，停稳后高档回来了直接换上，中间不需要重画。
/// 反过来按页存一格的话，"降档"就得<b>覆盖</b>掉正在显示的那一档，于是移动结束瞬间画面会空一帧。
/// </para>
/// </summary>
internal sealed class PdfPageCache : IDisposable
{
    /// <summary>
    /// 预算<b>256 MB</b>。从探针的数字反推：220 dpi 一页 17 MB，
    /// 所以这个预算装得下 15 页满分辨率、或者<b>两百多页</b>低档（1 MB 一页）。
    /// 换句话说"一本 300 页的文档在快速滚动时全程有图"，而"停在 300 dpi 细看"时
    /// 只有当前十几页是清楚的 —— 那正是我们要的：<b>清楚的范围跟着眼睛走</b>。
    /// </para>
    internal const long BudgetBytes = 256L * 1024 * 1024;

    private readonly record struct Key(int PageIndex, PdfResolutionPolicy.ResolutionTier Tier);

    private sealed class Entry
    {
        internal required BitmapImage Image { get; init; }
        internal required long Bytes { get; init; }
    }

    private readonly object _pdfium = new();
    private readonly PdfPageRasterizer _rasterizer = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly LinkedList<Key> _lru = [];
    private long _bytes;
    private bool _disposed;

    /// <summary>缓存里当前占多少字节（验收读它）。</summary>
    internal long TotalBytes => _bytes;

    /// <summary>
    /// 文档代号。<b>换一次文档就 +1</b>，请求带着它发出时的代号回来对账。
    /// <para>理由见 <see cref="Produce"/>：PDFium 的句柄是原生指针，释放之后再用是内存损坏，
    /// 而托管侧的判据只有这一个。</para>
    /// </summary>
    private int _generation;

    /// <summary>缓存里几格（验收读它）。</summary>
    internal int EntryCount => _entries.Count;

    /// <summary>打开一个 PDF。</summary>
    internal bool TryOpen(string path, out PdfDocumentHandle? document, out string? error)
    {
        lock (_pdfium)
        {
            if (_disposed) { document = null; error = "缓存已释放。"; return false; }

            // 换文档之前先把上一本文档的图全部释放：它们的尺寸/内容已经与新文档无关，
            // 而留着就是白占一份 256 MB 预算。
            if (_document is not null)
            {
                _document = null;
                _rasterizer.Close();
                ClearCore();
                // 早排队的那一批全都作废：它们攥的是即将被换掉的那份句柄。
                _generation++;
            }

            if (!_rasterizer.TryOpen(path, out document, out error)) return false;

            // 这行是承重的：Produce 在后台线程只读 _document，不回头问 rasterizer
            // （问了就得再进一次锁，而它正等着那把锁）。
            _document = document;
            _currentPage = 0;
            return true;
        }
    }

    /// <summary>当前文档的页数；没打开就是 0。</summary>
    internal int PageCount => _rasterizer.PageCount;

    /// <summary>
    /// <b>同步</b>取一格，取不到就<b>什么都不做</b>（返回 false）。
    /// <para>
    /// 这一条是"移动时不栅格化"能成立的机制：<b>缓存 miss 在移动期间不是错误</b>，
    /// 它只是"这一格现在没有"。调用方继续用手上那一档画。
    /// 早期那版让 miss 触发同步光栅化，于是滚动一卡 25 ms —— 而那 25 ms 全花在
    /// 一张用户 200 ms 后就滚过去的图上。
    /// </para>
    /// </summary>
    internal bool TryGet(int pageIndex, PdfResolutionPolicy.ResolutionTier tier, out BitmapImage? image)
    {
        lock (_pdfium)
        {
            if (_disposed || _rasterizer.PageCount == 0) { image = null; return false; }
            if (!_entries.TryGetValue(new Key(pageIndex, tier), out var entry))
            {
                image = null;
                return false;
            }

            Touch(new Key(pageIndex, tier));
            image = entry.Image;
            return true;
        }
    }

    /// <summary>
    /// 异步取一格：缓存命中就立刻返回，miss 则<b>在后台</b>光栅化。
    /// <para>
    /// <b>同一页的旧请求会被作废</b>（<see cref="PdfPageRequest.IsSuperseded"/>）：
    /// 用户在低档与高档之间来回时，先发的那一趟已经没意义了，而 PDFium 的渲染不能中断，
    /// 让它跑完只是浪费——更糟的是它落地时会<b>覆盖</b>掉更新的那一档。
    /// </para>
    /// </summary>
    internal PdfPageRequest Request(int pageIndex, PdfResolutionPolicy.ResolutionTier tier)
    {
        var request = new PdfPageRequest(pageIndex, tier, _generation);
        if (TryGet(pageIndex, tier, out var cached) && cached is not null)
        {
            request.Complete(cached);
            return request;
        }

        // 光栅化必须在锁外跑（它要 25 ms），但 PDFium 只允许一条链在动 —— 所以用一把
        // 单独的闸，而不是把整个缓存锁住：那样一次 25 ms 的渲染会把 UI 线程的
        // TryGet 一起卡住，而 UI 线程每个视口变动都要问一次缓存。
        _ = Task.Run(() => Produce(request));
        return request;
    }

    private void Produce(PdfPageRequest request)
    {
        if (request.IsSuperseded) return;

        PdfPageRaster? raster;
        lock (_pdfium)
        {
            if (_disposed) return;
            if (_document is not { } document) return;

            // 代号守卫：拿文档的代号和请求的代号比。
            //
            // 少了这一句会**访问违例**而不是抛异常：换文档时上一本文档的
            // FPDF_CloseDocument 已经跑过，而一个早排队、刚开跑的 Produce 还攥着
            // 那份已释放的句柄 —— 交给 PDFium 的是野指针，外壳读到的是
            // 0xC0000005 的硬崩，托管侧连 catch 都接不到。
            //
            // 这类"释放之后还有人在用"在托管代码里通常表现为 ObjectDisposedException，
            // 在原生句柄上就是内存损坏 —— 所以它必须**在进原生之前**挡掉，
            // 不能指望那里抛得出托管异常。
            if (request.Generation != _generation) return;

            raster = _rasterizer.Rasterize(document, request.PageIndex, PdfResolutionPolicy.DpiOf(request.Tier), _rasterizer.PageSize(request.PageIndex));
        }

        if (raster is null) { request.Fail(); return; }

        BitmapImage image;
        try
        {
            // FromPixels 会**拷贝**一份字节，所以这一刻是两份。17 MB × 2 是这一档的峰值开销，
            // 也就是为什么预算按"一份"算而淘汰要按"两份"看。
            image = BitmapImage.FromPixels(raster.Pixels, raster.Width, raster.Height);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException)
        {
            request.Fail();
            return;
        }

        // 光栅化期间用户可能又滚了：那么这一趟落地时已经过期，**直接扔掉**，
        // 不进缓存 —— 留着它就是一份没人要的 17 MB。
        if (request.IsSuperseded || _disposed)
        {
            image.Dispose();
            request.Fail();
            return;
        }

        lock (_pdfium)
        {
            if (_disposed) { image.Dispose(); return; }
            Store(new Key(request.PageIndex, request.Tier), image, (long)raster.Pixels.Length);
        }

        request.Complete(image);
    }

    private PdfDocumentHandle? _document;

    private void Store(Key key, BitmapImage image, long bytes)
    {
        if (_entries.Remove(key, out var previous))
        {
            _lru.Remove(key);
            _bytes -= previous.Bytes;
            previous.Image.Dispose();
        }

        _entries[key] = new Entry { Image = image, Bytes = bytes };
        _lru.AddLast(key);
        _bytes += bytes;
        Evict();
    }

    /// <summary>
    /// 淘汰到预算以内：<b>从最久没用的开始逐出</b>，而<b>当前页的任何档位都不动</b>。
    /// <para>
    /// 保护当前页的理由很具体：淘汰掉它会让画面<b>当场空一帧</b>，
    /// 而那一帧恰好出现在用户停下鼠标、最在意清晰度的时候。
    /// 所以这里<b>宁可短暂超预算</b>，也不接受"预算内但糊"。
    /// </para>
    /// </summary>
    private void Evict()
    {
        var node = _lru.First;
        while (_bytes > BudgetBytes && node is not null)
        {
            var next = node.Next;
            if (!IsProtected(node.Value) && _entries.Remove(node.Value, out var entry))
            {
                _bytes -= entry.Bytes;
                entry.Image.Dispose();
                _lru.Remove(node);
            }

            node = next;
        }
    }

    /// <summary>当前页（正在被看的那一页）的所有档位都不淘汰。</summary>
    private bool IsProtected(Key key) => key.PageIndex == _currentPage;

    private int _currentPage;

    /// <summary>告诉缓存"用户现在在看第几页"，那一页的任何档位都不参与淘汰。</summary>
    internal void SetCurrentPage(int pageIndex) => _currentPage = pageIndex;

    private void Touch(Key key)
    {
        if (_lru.Remove(key)) _lru.AddLast(key);
    }

    /// <summary>关掉当前文档，清空缓存。</summary>
    internal void Close()
    {
        lock (_pdfium)
        {
            _document = null;
            _rasterizer.Close();
            ClearCore();
            // 换文档代号：晚回来的那一批 Produce 一进 PDFium 之前就对不上号，直接作废。
            _generation++;
        }
    }

    private void ClearCore()
    {
        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
        _lru.Clear();
        _bytes = 0;
    }

    public void Dispose()
    {
        lock (_pdfium)
        {
            if (_disposed) return;
            _disposed = true;
            _document = null;
            _rasterizer.Close();
            ClearCore();
            // 同上：这一份文档已经彻底没了，任何还攥着它的请求都必须作废。
            _generation++;
        }
    }

    /// <summary>一次光栅化请求的句柄：能问"好了吗"，也能问"你还作数吗"。</summary>
    internal sealed class PdfPageRequest
    {
        private readonly TaskCompletionSource<BitmapImage> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _superseded;
        private int _settled;

        /// <summary>
        /// 这一趟落地了（成功或失败都发一次）。
        /// <para>
        /// <b>发在完成之后而不是之前</b>：换 Source 必须回到 UI 线程，
        /// 而光栅化跑在后台线程 —— 用 <c>RunContinuationsAsynchronously</c> 的
        /// <c>TaskCompletionSource</c> + 在这里转发，续体就不会抢在后台线程上直接动 UI。
        /// </para>
        /// </summary>
        internal event Action? Completed;

        internal PdfPageRequest(int pageIndex, PdfResolutionPolicy.ResolutionTier tier, int generation)
        {
            PageIndex = pageIndex;
            Tier = tier;
            Generation = generation;
        }

        /// <summary>这份请求属于哪一本文档。换文档后对不上号就作废（理由见 <see cref="PdfPageCache.Produce"/>）。</summary>
        internal int Generation { get; }

        internal int PageIndex { get; }

        internal PdfResolutionPolicy.ResolutionTier Tier { get; }

        /// <summary>这一趟<b>还是不是当前要的那一趟</b>。</summary>
        internal bool IsSuperseded => Volatile.Read(ref _superseded) != 0;

        /// <summary>落地了没有。</summary>
        internal bool IsCompleted => _completion.Task.IsCompleted;

        /// <summary>拿结果（没好返回 null）。</summary>
        internal BitmapImage? Result => _completion.Task.IsCompletedSuccessfully ? _completion.Task.Result : null;

        /// <summary>用户已经看过/滚走了，这一趟作废。</summary>
        internal void Supersede() => Interlocked.Exchange(ref _superseded, 1);

        internal void Complete(BitmapImage image) => Settle(image);

        internal void Fail() => Settle(null);

        private void Settle(BitmapImage? image)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
            {
                _completion.TrySetResult(image!);
                Completed?.Invoke();
            }
        }
    }
}
