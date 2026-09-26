using System.Diagnostics;

namespace LanStartWrite.Inkcanvas.Pdf;

/// <summary>光栅化一页的结果：托管 BGRA 像素 + 尺寸 + 耗时。</summary>
/// <param name="Pixels">
/// BGRA8888 紧密排列，可直接喂 <c>BitmapImage.FromPixels(pixels, width, height)</c>。
/// <para><b>这一层刻意不引用 Jalium</b>：产出像素的代码不该知道框架，
/// 于是阶段 0 的探针能单独编译它（不带窗口、不带 433 条断言）。</para>
/// </param>
/// <param name="Width">像素宽。</param>
/// <param name="Height">像素高。</param>
/// <param name="Dpi">实际使用的 DPI（可能被夹或取整过）。</param>
/// <param name="Milliseconds">耗时，用来在探针与验收里当读数。</param>
internal sealed record PdfPageRaster(byte[] Pixels, int Width, int Height, int Dpi, long Milliseconds);

/// <summary>
/// 把 PDF 的一页画成像素。<b>纯计算，没有 UI、没有缓存、没有调度</b> ——
/// 后三样分别是 <see cref="PdfPageCache"/> 与 <see cref="PdfResolutionPolicy"/> 的事。
/// <para>拆这么干净是有意的：这个类可以被一个不带窗口的探针直接调用（阶段 0 就是这么用的），
/// 于是"光栅化慢不慢"这件事<b>不依赖界面跑起来</b>就能量。</para>
/// <para>
/// <b>非线程安全</b>：PDFium 的文档与页对象都不是线程安全的。所有调用必须经
/// <see cref="PdfPageCache"/> 的锁串起来 —— 那个类是这个进程里<b>唯一</b>碰 PDFium 的地方。
/// </para>
/// </summary>
internal sealed class PdfPageRasterizer
{
    /// <summary>页尺寸，单位<b>点</b>（1/72 英寸）。</summary>
    internal readonly record struct PageSizePoints(double WidthPt, double HeightPt)
    {
        internal static readonly PageSizePoints Empty = new(0, 0);

        internal bool IsUsable => WidthPt > 0 && HeightPt > 0 && double.IsFinite(WidthPt) && double.IsFinite(HeightPt);
    }

    /// <summary>探针与日志的出口（线上不用它）。</summary>
    internal static Action<string>? Instrumentation = static _ => { };

    private readonly object _gate = new();
    private PdfiumNative.DocumentHandle? _document;
    private PageSizePoints[] _pageSizes = [];
    private string? _path;

    /// <summary>
    /// 全部页的尺寸，<b>打开时一次取全</b>。
    /// <para>
    /// 除了"少一轮 Load/Close"（理由见 <see cref="Rasterize"/>），更实际的理由是：
    /// 一次几百页的文档，布局要的就是这份尺寸表，而反复问 PDFium 既慢又在换页时踩到那个崩溃。
    /// </para>
    /// </summary>
    internal IReadOnlyList<PageSizePoints> PageSizes => _pageSizes;

    /// <summary>当前打开的文档路径（探针与设置页要念它）。</summary>
    internal string? OpenedPath => _path;

    /// <summary>当前文档的页数；没打开就是 0。</summary>
    internal int PageCount
    {
        get
        {
            lock (_gate) return _document?.PageCount ?? 0;
        }
    }

    /// <summary>打开一个 PDF。<b>失败不抛</b>：一个打不开的文件不该把整个功能带走。</summary>
    internal bool TryOpen(string path, out PdfDocumentHandle? document, out string? error)
    {
        lock (_gate)
        {
            document = null;
            error = null;
            try
            {
                PdfiumNative.Initialize();
                var handle = new PdfiumNative.DocumentHandle(path);
                _document?.Dispose();
                _document = handle;
                _path = path;

                // 尺寸一次取全（理由见 PageSizes）。缺尺寸的页退到 A4 ——
                // 宁可排版得不准，也不要在这里抛：一页坏掉不该让整份文档打不开。
                _pageSizes = new PageSizePoints[handle.PageCount];
                for (var i = 0; i < handle.PageCount; i++)
                {
                    try
                    {
                        using var page = new PdfiumNative.PageHandle(handle.Handle, i);
                        var (w, h) = page.SizePoints;
                        _pageSizes[i] = new PageSizePoints(w, h);
                    }
                    catch (PdfNativeException)
                    {
                        _pageSizes[i] = new PageSizePoints(595, 842);
                    }
                }

                document = new PdfDocumentHandle(this, handle.PageCount);
                return true;
            }
            catch (Exception ex) when (ex is PdfNativeException or DllNotFoundException
                or EntryPointNotFoundException or BadImageFormatException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>页尺寸。PDFium 的单位是<b>点</b>，这一层不做换算 —— 换算只在"要几个像素"那一处发生。</summary>
    internal PageSizePoints PageSize(int pageIndex)
    {
        lock (_gate)
        {
            if (_document is null) return PageSizePoints.Empty;
            using var page = new PdfiumNative.PageHandle(_document.Handle, pageIndex);
            var (w, h) = page.SizePoints;
            return new PageSizePoints(w, h);
        }
    }

    /// <summary>
    /// 按 <paramref name="dpi"/> 光栅化一页。
    /// <para>
    /// <b>页尺寸由调用方给</b>（<paramref name="sizePoints"/>），不在这里重新查 ——
    /// 查一次意味着 <c>FPDF_LoadPage</c> + <c>FPDF_ClosePage</c> 一轮，
    /// 而"先 Load 再 Close、然后按<b>页号</b>去渲染"这个序列实测会让 PDFium
    /// 在换页时崩：第 1 页成功、第 2 页 <b>0xC0000005 硬崩</b>（托管层 catch 不到，整个进程走）。
    /// 单线程、同一份文件、探针与窗口里都复现，所以它是调用序列的问题，不是并发或生命周期。
    /// 尺寸本来就在打开文档时一次取全了（<see cref="PageSizes"/>），每次重查纯属多余。
    /// </para>
    /// <para>
    /// <b>像素数按 DPI 线性算</b>（点 → 像素 = dpi/72），不做任何"按屏幕缩放"的猜测 ——
    /// 那是 <see cref="PdfResolutionPolicy"/> 的判断，它把该要的那一档 DPI 递进来。
    /// </para>
    /// <para>
    /// <b>宽高各自夹到 1..8191</b>：PDFium 用 <c>int</c> 收宽高，而一份畸形 PDF 的
    /// <c>MediaBox</c> 可以是天文数字 —— 不夹就是一次 4 GB 的 <c>Marshal.AllocHGlobal</c>。
    /// </para>
    /// </summary>
    internal PdfPageRaster? Rasterize(PdfDocumentHandle document, int pageIndex, int dpi, PageSizePoints? sizePoints = null)
    {
        lock (_gate)
        {
            if (_document is null) return null;

            // 页号越界**在进原生之前**挡掉。
            //
            // FPDF_RenderPageBitmap 收的是一个页号，越界时 PDFium 内部会拿到空页句柄再
            // 解引用 —— 那是 0xC0000005 的**硬崩**，托管层的 try/catch 接不到，
            // 整个进程跟着走。所以凡是能算出来的越界，都要在这一行之前解决。
            if ((uint)pageIndex >= (uint)_document.PageCount) return null;

            var size = sizePoints ?? PageSize(pageIndex);
            if (!size.IsUsable) return null;

            var scale = Math.Clamp(dpi, 24, 1200) / 72.0;
            var width = Clamp((int)Math.Round(size.WidthPt * scale));
            var height = Clamp((int)Math.Round(size.HeightPt * scale));
            if (width <= 0 || height <= 0) return null;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var bitmap = new PdfiumNative.BitmapHandle(width, height);
                // 整页铺满：起点 0、尺寸就是整块位图。rotate=0 是因为**页的朝向由我们自己管**
                // （用户能转 90°，那是我们的变换，不该被 PDF 的 /Rotate 又转一次）。
                Instrumentation?.Invoke($"[pdf] render page={pageIndex} dpi={dpi} {width}x{height}");
                bitmap.RenderPage(pageIndex, PdfiumNative.RenderContentOnly);
                var pixels = bitmap.CopyToManaged();
                stopwatch.Stop();
                return new PdfPageRaster(pixels, width, height, dpi, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is PdfNativeException or OutOfMemoryException)
            {
                Instrumentation?.Invoke($"[pdf] 第 {pageIndex + 1} 页 {dpi}dpi 光栅化失败：{ex.Message}");
                return null;
            }
        }
    }

    private static int Clamp(int value) => Math.Clamp(value, 1, 8191);

    internal void Close()
    {
        lock (_gate)
        {
            _document?.Dispose();
            _document = null;
            _path = null;
        }
    }
}

/// <summary>
/// 一个已打开的 PDF 文档的句柄。<b>只读</b>的那些数在构造时取好，
/// 免得每次问一句都要回到 rasterizer 那里拿锁。
/// </summary>
internal sealed class PdfDocumentHandle
{
    internal PdfDocumentHandle(PdfPageRasterizer owner, int pageCount)
    {
        Owner = owner;
        PageCount = pageCount;
    }

    internal PdfPageRasterizer Owner { get; }
    internal int PageCount { get; }

    internal PdfPageRasterizer.PageSizePoints PageSize(int pageIndex) =>
        Owner.PageSize(pageIndex);
}
