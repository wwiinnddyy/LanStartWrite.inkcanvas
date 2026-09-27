using System.Diagnostics;
using System.Runtime.InteropServices;
using LanStartWrite.Inkcanvas.Pdf;

namespace LanStartWrite.Inkcanvas.Tools;

/// <summary>
/// 阶段 0 探针：<b>证明 PDFium 真的能加载并光栅化</b>，顺便量出各 DPI 的真实耗时。
/// <para>
/// 整个 PDF 功能的地基是这一条。上面所有设计（分辨率阶梯、预加载、缓存预算）
/// 都要拿这些数字当输入 ——「降档要降到多少」取决于一页 200 DPI 要几毫秒，
/// 而这个数字<b>每台机器、每个 PDF 都不一样</b>。先量出来，后面才不是在猜。
/// </para>
/// <para>
/// 它编译的是<b>主工程的同一份源码</b>（csproj 里用 <c>Link</c> 链进来，不复制），
/// 所以量出来的数字对主应用有效；改一处两边同时变。
/// </para>
/// <para>用法：<c>dotnet run --project tools/PdfProbe -- &lt;某个.pdf&gt;</c></para>
/// </summary>
internal static class PdfProbe
{
    internal static int Run(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine("用法: PdfProbe <某个.pdf>");
            return 2;
        }

        // 最小复现模式：只做"开档 → 渲第 0 页 → 渲第 1 页"，什么尺寸查询、填充、
        // 插桩都不带。用来判定崩溃到底属于哪一步。
        if (args.Contains("--minimal", StringComparer.OrdinalIgnoreCase)) return RunMinimal(path);

        // 对照模式：**只**走 FPDF_RenderPageBitmapWithMatrix（页句柄由我们自己拿）。
        // 与 --minimal 的差别只有渲染入口，其余逐字相同 —— 所以两者一绿一红
        // 就是"崩在旧入口内部那次 FPDF_LoadPage 上"的直接证据。
        if (args.Contains("--matrix", StringComparer.OrdinalIgnoreCase)) return RunMatrix(path);

        // 墨量模式：白底 → 渲 → 数非白像素。**只渲第 0 页**（第 1 页起那个入口会硬崩）。
        // 问的是"渲染到底有没有发生"，而不是"崩不崩"——
        // 崩与不崩都不蕴含画上了，而"画上了"才是判据。
        if (args.Contains("--ink", StringComparer.OrdinalIgnoreCase)) return RunInk(path);

        // 页句柄体检：把"LoadPage 返回的到底是不是一个能用的页"这件事问清楚。
        // 此前一直只判 `!= 0`（那不是有效性，只是一个非空指针），
        // 而页尺寸一直是通过另一条路（PdfPageRasterizer）读的，两处从没在同一进程里对过。
        if (args.Contains("--page", StringComparer.OrdinalIgnoreCase)) return RunPageProbe(path);

        if (args.Contains("--formats", StringComparer.OrdinalIgnoreCase)) return RunFormats(path);

        if (args.Contains("--png", StringComparer.OrdinalIgnoreCase)) return RunPng(path, args);

        // 文本层体检：**只走解析，不碰光栅化**。它回答的是
        // "PDFium 到底读不读得懂这份文档的页" ——
        // 读得懂而画不出，问题就在光栅化那一侧；读不懂，问题在打开那一侧。
        if (args.Contains("--text", StringComparer.OrdinalIgnoreCase)) return RunText(path);

        return RunFull(path);
    }

    private static int RunText(string path)
    {
        PdfiumNative.Initialize();
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[text] pages={document.PageCount}");

        for (var i = 0; i < Math.Min(document.PageCount, 3); i++)
        {
            var page = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, i);
            var count = PdfiumNative.PdfiumNativeForProbe.TextCharCount(page);
            Console.WriteLine($"[text] 第{i + 1}页 字符数={count}");
            if (count > 0)
            {
                var text = PdfiumNative.PdfiumNativeForProbe.TextOf(page, 90);
                Console.WriteLine($"[text]   开头：{text.Replace("\r", " ").Replace("\n", " ")}");
            }
            if (page != 0) PdfiumNative.PdfiumNativeForProbe.ClosePage(page);
        }

        document.Dispose();
        return 0;
    }

    /// <summary>把指定几页渲成 PNG 存到临时目录，然后人眼确认"画出来的到底对不对"。</summary>
    /// <remarks>
    /// 前面全是数字（墨量百分比），而数字有个坏处：<b>错的内容也能给出好看的数字</b>。
    /// 通道顺序错一位、上下颠倒、缩放取反——都还是"有很多非白像素"。
    /// 所以这里落成 PNG 让人看一眼；顺带验证第 1 页起还会不会硬崩。
    /// </remarks>
    private static int RunPng(string path, string[] args)
    {
        PdfiumNative.Initialize();
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[png] pages={document.PageCount}");

        var pageArg = args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.Ordinal));
        var pages = pageArg is null
            ? Enumerable.Range(0, Math.Min(document.PageCount, 3)).ToArray()
            : new[] { int.Parse(pageArg["--page=".Length..]) };

        foreach (var page in pages)
        {
            // 页句柄开一次、贯穿"量尺寸 + 渲染"，最后关一次。
            // 渲染函数要的就是它，所以**不能**像先前那样 LoadPage 完就丢在旁边。
            var handle = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, page);
            if (handle == 0) { Console.WriteLine($"[png] 第{page + 1}页 句柄为空，跳过"); continue; }
            var size = PdfiumNative.PdfiumNativeForProbe.PageSizeOf(handle);
            var w = (int)size.Width;
            var h = (int)size.Height;

            // 缓冲归 PDFium，stride 读它报的 —— 三个量（缓冲归属、stride、每像素字节数）
            // 错任何一个，读数都会与"没渲染"完全一样。
            var bitmap = PdfiumNative.PdfiumNativeForProbe.CreateOwnBufferBitmap(w, h);
            if (bitmap == 0) { Console.WriteLine($"[png] 第{page + 1}页 建位图失败"); continue; }
            var stride = PdfiumNative.PdfiumNativeForProbe.GetStride(bitmap);
            var buf = PdfiumNative.PdfiumNativeForProbe.GetBuffer(bitmap);
            Console.WriteLine($"[png] 第{page + 1}页 {w}×{h} stride={stride}（4×w={4 * w}）");

            PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);
            try
            {
                PdfiumNative.PdfiumNativeForProbe.RenderPage(bitmap, handle, w, h);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[png] 第{page + 1}页 渲染抛了托管异常：{ex.GetType().Name} {ex.Message}");
                PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
                PdfiumNative.PdfiumNativeForProbe.ClosePage(handle);
                continue;
            }

            // BGRA 的前三字节就是 B,G,R，与下面那两个按 3 字节读的助手兼容。
            var bgra = PdfiumNative.PdfiumNativeForProbe.ReadBack(buf, stride * h);
            Console.WriteLine($"[png] 第{page + 1}页 非白像素 {CountInkBgr(bgra, stride, w, h):F2}%");

            var dest = Path.Combine(Path.GetTempPath(), $"pdfpage{page + 1}.png");
            File.WriteAllBytes(dest, EncodePng(bgra, stride, w, h));
            Console.WriteLine($"[png] 写入 {dest}（{new FileInfo(dest).Length / 1024.0:N0} KB）");

            PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
            PdfiumNative.PdfiumNativeForProbe.ClosePage(handle);
        }

        document.Dispose();
        return 0;
    }

    /// <summary>BGR 缓冲的非白像素百分比（3 字节/像素）。</summary>
    private static double CountInkBgr(byte[] p, int stride, int w, int h)
    {
        long ink = 0;
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * 3;
                if (p[i] != 255 || p[i + 1] != 255 || p[i + 2] != 255) ink++;
            }
        }
        return 100.0 * ink / ((long)w * h);
    }

    /// <summary>最小 PNG 编码器：BGR 缓冲 → RGB PNG（每行一个 0 过滤字节，zlib 压）。</summary>
    private static byte[] EncodePng(byte[] bgr, int stride, int w, int h)
    {
        var raw = new byte[h * (w * 3 + 1)];
        for (var y = 0; y < h; y++)
        {
            var dst = y * (w * 3 + 1);
            raw[dst] = 0;                       // filter: None
            var src = y * stride;
            for (var x = 0; x < w; x++)
            {
                var s = src + x * 3;
                var d = dst + 1 + x * 3;
                raw[d] = bgr[s + 2];            // R
                raw[d + 1] = bgr[s + 1];        // G
                raw[d + 2] = bgr[s];            // B
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len);
            ms.Write(len);
            var td = new byte[4 + data.Length];
            for (var i = 0; i < 4; i++) td[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            ms.Write(td);
            var crc = Crc32(td);
            if (BitConverter.IsLittleEndian) Array.Reverse(crc);
            ms.Write(crc);
        }

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w);
        WriteBe(ihdr, 4, h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // color type: truecolor
        Chunk("IHDR", ihdr);

        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                zs.Write(raw, 0, raw.Length);
            }
            idat = z.ToArray();
        }
        Chunk("IDAT", idat);
        Chunk("IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteBe(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static byte[] Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        var v = c ^ 0xFFFFFFFFu;
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    /// <summary>
    /// 逐个 <c>FPDFBitmap_*</c> 格式试，只渲第 0 页。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>三个必须同时对的量</b>，错任何一个读数就与"没渲染"完全一样：
    /// 缓冲归 PDFium（<c>FPDFBitmap_Create</c>，否则 <c>CreateEx</c> 要我们自己保证
    /// stride 对）、<b>stride 读 PDFium 报的</b>（<c>FPDFBitmap_GetStride</c> ——
    /// 实测 <c>BGR</c> 配 612 宽时它给的是 2448 而不是 1836，即按 4 字节/像素对齐，
    /// 自己按 <c>3×w</c> 算必然错位）、以及<b>每像素几个字节要跟着格式走</b>。
    /// </para>
    /// <para>
    /// 第一版这三样只对了两样，得出"Gray 75% / BGR 25% / BGRx 0% / BGRA 0%"，
    /// 看着像"格式决定一切"，其实那两个非零数全是<b>错位读出来的垃圾</b>。
    /// 判据只有一个：<b>按格式读对之后，非白像素是不是仍然为 0</b>。
    /// </para>
    /// </remarks>
    private static int RunFormats(string path)
    {
        PdfiumNative.Initialize();
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[fmt] pages={document.PageCount}");

        const int w = 612, h = 792;
        var loaded = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, 0);
        if (loaded == 0) { Console.WriteLine("[fmt] 页句柄为空"); return 1; }

        (int Bpp, string Name)[] formats =
        {
            (1, "Gray(1)"),
            (3, "BGR(2)"),
            (4, "BGRx(3)"),
            (4, "BGRA(4)"),
        };

        foreach (var (bpp, name) in formats)
        {
            var code = bpp == 1 ? 1 : bpp == 3 ? 2 : bpp == 4 ? name == "BGRx(3)" ? 3 : 4 : 4;
            var bitmap = PdfiumNative.PdfiumNativeForProbe.CreateOwnBufferBitmapOfFormat(w, h, code);
            if (bitmap == 0) { Console.WriteLine($"[fmt] {name,-10} FPDFBitmap_Create 返回空"); continue; }

            var stride = PdfiumNative.PdfiumNativeForProbe.GetStride(bitmap);
            var buf = PdfiumNative.PdfiumNativeForProbe.GetBuffer(bitmap);
            PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);
            var afterFill = CountInkOfFormat(
                PdfiumNative.PdfiumNativeForProbe.ReadBack(buf, stride * h), stride, w, h, bpp);

            PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);
            PdfiumNative.PdfiumNativeForProbe.RenderPage(bitmap, loaded, w, h);
            var afterRender = CountInkOfFormat(
                PdfiumNative.PdfiumNativeForProbe.ReadBack(buf, stride * h), stride, w, h, bpp);

            Console.WriteLine($"[fmt] {name,-10} stride={stride,5}（w×{bpp}={w * bpp}）" +
                              $"  FillRect后={afterFill,6:F2}%  渲染后={afterRender,6:F2}%");

            PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
        }

        document.Dispose();
        return 0;
    }

    /// <summary>按每像素 <paramref name="bpp"/> 字节、<paramref name="stride"/> 读的非白像素百分比。</summary>
    private static double CountInkOfFormat(byte[] p, int stride, int w, int h, int bpp)
    {
        long ink = 0;
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * bpp;
                if (bpp == 1)
                {
                    if (p[i] != 255) ink++;
                }
                else if (p[i] != 255 || p[i + 1] != 255 || p[i + 2] != 255) ink++;
            }
        }
        return 100.0 * ink / ((long)w * h);
    }

    private static int RunPageProbe(string path)
    {
        PdfiumNative.Initialize();
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[page] pages={document.PageCount}");

        for (var i = 0; i < Math.Min(document.PageCount, 4); i++)
        {
            var loaded = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, i);
            var size = PdfiumNative.PdfiumNativeForProbe.PageSizeOf(loaded);
            Console.WriteLine($"[page] 第{i + 1}页 句柄=0x{loaded:X} 宽高={size.Width}×{size.Height}");
            if (loaded != 0) PdfiumNative.PdfiumNativeForProbe.ClosePage(loaded);
        }

        // 反面对照：随便编一个句柄（比如把文档句柄当页句柄传），
        // 拿它的宽高看一眼 —— 若那也返回"像样"的数字，
        // 就说明 FPDF_GetPageWidth 对垃圾指针**不报错**，于是"尺寸读得对"根本不是有效性的证据。
        var bogus = document.Handle;
        var bogusSize = PdfiumNative.PdfiumNativeForProbe.PageSizeOf(bogus);
        Console.WriteLine($"[page] 反面对照：把文档句柄当页句柄 → 宽高={bogusSize.Width}×{bogusSize.Height}");

        document.Dispose();
        return 0;
    }

    private static int RunInk(string path)
    {
        PdfiumNative.Initialize();
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[ink] pages={document.PageCount}");
        Console.WriteLine($"[ink] 实际加载的 pdfium：{PdfiumNative.PdfiumNativeForProbe.LoadedModulePath()}");

        const float scale = 2f;
        var w = (int)(612 * scale);
        var h = (int)(792 * scale);
        var stride = w * 4;
        var buffer = Marshal.AllocHGlobal(stride * h);
        var bitmap = PdfiumNative.PdfiumNativeForProbe.CreateBitmap(w, h, buffer, stride);

        // 先证明 FillRect 真的能写进我们的缓冲：否则后面全是空中楼阁。
        PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);
        var before = CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(buffer, stride * h), w, h);
        Console.WriteLine($"[ink] 只 FillRect 白（应 0.00%）：{before:F2}%");

        // 尺子要先校准：填黑必须是 100%。不校准的话，"0.00%"有两种可能 ——
        // 真的没画上，或者尺子坏了 —— 而这两种在读数上一模一样。
        PdfiumNative.PdfiumNativeForProbe.FillBlack(bitmap, w, h);
        var cal = CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(buffer, stride * h), w, h);
        Console.WriteLine($"[ink] 校准：FillRect 黑（应 100.00%）：{cal:F2}%");

        PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);

        var loaded = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, 0);
        Console.WriteLine($"[ink] loadpage 0 = {(loaded == 0 ? "NULL" : loaded.ToString())}");
        if (loaded == 0) { Console.WriteLine("[ink] 页句柄为空，停"); document.Dispose(); return 1; }
        // 刻意**不**在这里 ClosePage：渲染要用的就是它，
        // 提前关掉就是拿一个野指针去画（那正是当初崩在第 2 页上的形状）。

        // 走会崩的那个入口，参数取最小：flags=0、rotate=0、整幅。
        PdfiumNative.PdfiumNativeForProbe.RenderPage(bitmap, loaded, w, h);
        var after = CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(buffer, stride * h), w, h);
        Console.WriteLine($"[ink] 第 0 页 RenderPage 之后（我们的缓冲）：{after:F2}%");

        PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
        Marshal.FreeHGlobal(buffer);

        // ── 对照：缓冲归 PDFium 自己 ────────────────────────────────────────
        // 到这里为止，我们那块缓冲已经被证明"可写"（FillRect 写得进去），
        // 于是"没画上"要么是渲染没发生，要么是画到别处去了。
        // 换成 FPDFBitmap_Create（PDFium 自己分配）就能把后一种彻底排掉：
        // 那块缓冲 PDFium 没有任何理由不往里写。
        var own = PdfiumNative.PdfiumNativeForProbe.CreateOwnBufferBitmap(w, h);
        if (own == 0) { Console.WriteLine("[ink] FPDFBitmap_Create 失败"); document.Dispose(); return 1; }
        var ownStride = PdfiumNative.PdfiumNativeForProbe.GetStride(own);
        var ownBuf = PdfiumNative.PdfiumNativeForProbe.GetBuffer(own);
        Console.WriteLine($"[ink] 自有缓冲 stride={ownStride}（我们要 {stride}）buffer=0x{ownBuf:X}");

        PdfiumNative.PdfiumNativeForProbe.FillWhite(own, w, h);
        Console.WriteLine($"[ink] 自有缓冲 FillRect 白（应 0.00%）：" +
            $"{CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(ownBuf, ownStride * h), w, h):F2}%");

        PdfiumNative.PdfiumNativeForProbe.RenderPage(own, loaded, w, h);
        Console.WriteLine($"[ink] 自有缓冲 RenderPage 第 0 页：" +
            $"{CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(ownBuf, ownStride * h), w, h):F2}%");

        PdfiumNative.PdfiumNativeForProbe.ClosePage(loaded);

        PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(own);
        document.Dispose();
        return 0;
    }

    private static int RunMatrix(string path)
    {
        PdfiumNative.Initialize();
        Console.WriteLine("[mtx] init ok");
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[mtx] open ok, pages={document.PageCount}");

        // 2 倍放大，这样"画没画上"不会和抗锯齿的浅灰混在一起。
        const float scale = 2f;
        const int width = 612, height = 792;
        var w = (int)(width * scale);
        var h = (int)(height * scale);
        var stride = w * 4;

        for (var page = 0; page < 3; page++)
        {
            if ((uint)page >= (uint)document.PageCount) break;
            var buffer = Marshal.AllocHGlobal(stride * h);
            var bitmap = PdfiumNative.PdfiumNativeForProbe.CreateBitmap(w, h, buffer, stride);
            // 先刷白：不刷的话缓冲是未初始化内存，"非白像素数"就成了随机数。
            PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);

            var loaded = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, page);
            Console.WriteLine($"[mtx] loadpage {page} = {(loaded == 0 ? "NULL" : loaded.ToString())}");
            if (loaded == 0) { Console.WriteLine($"[mtx] page {page} 句柄为空，停"); break; }

            // 不用 RenderPage（那个会崩），只走 matrix 入口。
            // 两种矩阵布局各渲一次：6 与 9 只差"PDFium 读几个 float"，
            // 而读少了不报错，只是越界读到栈垃圾、内容被映射到图外 → 整页空白。
            PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);
            PdfiumNative.PdfiumNativeForProbe.RenderPageWithMatrix(bitmap, loaded, 0, scale);
            var ink6 = CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(buffer, stride * h), w, h);
            Console.WriteLine($"[mtx] page {page} 6-float 矩阵 非白像素 {ink6:F2}%");

            PdfiumNative.PdfiumNativeForProbe.FillWhite(bitmap, w, h);
            PdfiumNative.PdfiumNativeForProbe.RenderPageWithMatrix9(bitmap, loaded, 0, scale);
            var ink9 = CountInk(PdfiumNative.PdfiumNativeForProbe.ReadBack(buffer, stride * h), w, h);
            Console.WriteLine($"[mtx] page {page} 9-float 矩阵 非白像素 {ink9:F2}%");

            PdfiumNative.PdfiumNativeForProbe.ClosePage(loaded);
            PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
            Marshal.FreeHGlobal(buffer);
        }

        document.Dispose();
        Console.WriteLine("[mtx] all ok");
        return 0;
    }

    private static int RunMinimal(string path)
    {
        PdfiumNative.Initialize();
        Console.WriteLine("[min] init ok");
        var document = new PdfiumNative.DocumentHandle(path);
        Console.WriteLine($"[min] open ok, pages={document.PageCount}");

        for (var page = 0; page < 3; page++)
        {
            if ((uint)page >= (uint)document.PageCount) break;
            const int width = 612, height = 792, stride = width * 4;
            var buffer = Marshal.AllocHGlobal(stride * height);
            var bitmap = PdfiumNative.PdfiumNativeForProbe.CreateBitmap(width, height, buffer, stride);
            Console.WriteLine($"[min] bitmap for page {page} ok");

            // 关键变量：先把这一页 LoadPage 出来并**保持打开**，再渲染。
            var loaded = PdfiumNative.PdfiumNativeForProbe.LoadPage(document.Handle, page);
            Console.WriteLine($"[min] loadpage {page} = {(loaded == 0 ? "NULL" : loaded.ToString())}");

            // 传的是 loaded（页句柄），不是 page（页号）——
            // 页号 0 会被 PDFium 当成空指针直接返回，于是"渲染成功但一格没画"。
            PdfiumNative.PdfiumNativeForProbe.RenderPage(bitmap, loaded, width, height);
            Console.WriteLine($"[min] render page {page} ok");

            if (loaded != 0) PdfiumNative.PdfiumNativeForProbe.ClosePage(loaded);
            PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
            Marshal.FreeHGlobal(buffer);
        }

        document.Dispose();
        Console.WriteLine("[min] all ok");
        return 0;
    }

    /// <summary>非白像素占全幅的百分比。</summary>
    /// <remarks>
    /// BGRA 下一个像素只要任一颜色通道不是 255 就算非白 —— 代价是抗锯齿边缘也算进去，
    /// 而那正是我们要的（"画上了没有"是问不出"画得好不好看"时的唯一判据）。
    /// 缓冲先被 <c>FPDFBitmap_FillRect</c> 刷成白：不刷的话缓冲是未初始化内存，
    /// 这个数就成了随机数，**而"随机数恰好很小"看起来与"没画上"一模一样**。
    /// </remarks>
    private static double CountInk(byte[] pixels, int width, int height)
    {
        long ink = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255) ink++;
        }
        return 100.0 * ink / ((long)width * height);
    }

    private static int RunFull(string path)
    {
        Console.WriteLine($"[probe] 平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}，" +
            $"{(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : RuntimeInformation.ProcessArchitecture.ToString())}");
        Console.WriteLine($"[probe] 文件 {path}（{new FileInfo(path).Length / 1024.0:N0} KB）");

        var rasterizer = new PdfPageRasterizer();
        PdfPageRasterizer.Instrumentation = line => Console.WriteLine(line);

        var total = Stopwatch.StartNew();
        if (!rasterizer.TryOpen(path, out var document, out var error))
        {
            Console.WriteLine($"[probe] 打不开：{error}");
            return 1;
        }

        Console.WriteLine($"[probe] 页数 {document!.PageCount}，打开耗时 {total.ElapsedMilliseconds} ms");

        var first = document.PageSize(0);
        Console.WriteLine($"[probe] 首页 {first.WidthPt:0.#}×{first.HeightPt:0.#} pt " +
            $"= {first.WidthPt / 72 * 96:0}×{first.HeightPt / 72 * 96:0} px @96dpi");

        // 尺寸是渲染的输入，而崩在渲染里 —— 崩之前必须先知道每一页的尺寸。
        for (var i = 0; i < document.PageCount; i++)
        {
            var s = document.PageSize(i);
            Console.WriteLine($"[probe] 尺寸 第{i + 1}页 {s.WidthPt:0.##}×{s.HeightPt:0.##} pt");
        }

        // 只量首页三档：探针要的是"数量级"，不是基准测试。
        foreach (var dpi in new[] { 72, 150, 220 })
        {
            var raster = rasterizer.Rasterize(document, 0, dpi);
            if (raster is null) { Console.WriteLine($"[probe] {dpi} dpi 失败"); continue; }

            var megabytes = (long)raster.Pixels.Length / 1024 / 1024;
            Console.WriteLine($"[probe] {dpi,3} dpi → {raster.Width}×{raster.Height}，" +
                $"{megabytes} MB，{raster.Milliseconds} ms");
        }

        // **每一页都渲一遍**：连续浏览的真正形态是"换页"，而只量首页三档是量不到的。
        // 早先那版探针只渲第 0 页，于是"渲完第 0 页再渲第 1 页"这条路径从来没被走过 ——
        // 而窗口里的胶片条恰好一上来就并着要好几页。
        var allPagesStart = Stopwatch.StartNew();
        var failed = new List<int>();
        for (var page = 0; page < document.PageCount; page++)
        {
            var raster = rasterizer.Rasterize(document, page, 72);
            if (raster is null) { failed.Add(page); continue; }
            Console.WriteLine($"[probe] 第 {page + 1} 页 {raster.Width}×{raster.Height}，{raster.Milliseconds} ms");
        }

        Console.WriteLine($"[probe] 全部 {document.PageCount} 页耗时 {allPagesStart.ElapsedMilliseconds} ms" +
            (failed.Count > 0 ? $"，失败 {failed.Count} 页：{string.Join(",", failed)}" : "，无失败"));

        // 内存那条：FromPixels 会**拷贝**一份，所以峰值是两份。
        Console.WriteLine("[probe] 注意 FromPixels 会再拷一份，所以瞬时内存约为上面那个数字的两倍。");
        Console.WriteLine($"[probe] 全程 {total.ElapsedMilliseconds} ms");
        return 0;
    }
}
