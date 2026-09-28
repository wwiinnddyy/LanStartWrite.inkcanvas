using System.Runtime.InteropServices;

namespace LanStartWrite.Inkcanvas.Pdf;

/// <summary>
/// PDFium 的<b>最小</b> P/Invoke 面 —— 只覆盖"打开文档、取页数、把一页画成位图"这条链。
/// <para>
/// <b>为什么自己写</b>：NuGet 上 <c>bblanchon.PDFium</c> 只装原生二进制，托管绑定在
/// <c>PDFtoImage</c> 里，而那一份会拖进 <b>SkiaSharp</b>。本项目的渲染器是 Jalium 的
/// Vello/DX12，再进一个图形栈只会带来两套纹理生命周期和一份多余的原生依赖。
/// 用户定的就是「直连、不引 Skia」，这一层是那个决定的全部代价 —— 而它只值二十来个签名。
/// </para>
/// <para>
/// <b>句柄一律是不透明指针</b>：PDFium 的 C API 全是 <c>void*</c>，这里用 <c>SafeHandle</c>
/// 之外的裸 <c>nint</c> + <c>IDisposable</c> 包装，因为这些对象的<b>创建与销毁必须成对且同线程</b>
/// （PDFium 的文档与位图不是线程安全的），用 SafeHandle 的终值器反而会在错误线程上释放。
/// </para>
/// <para>
/// <b>调用约定</b>：PDFium 导出的是 <c>extern "C"</c>，在 Windows 上统一 <c>CallingConvention.Cdecl</c>；
/// 64 位下 cdecl 与 stdcall 同为 Win64 约定，写死 Cdecl 是为了 32 位也正确。
/// </para>
/// </summary>
internal static class PdfiumNative
{
    private const string Lib = "pdfium";

    /// <summary>进程内是否已经初始化过（<see cref="Initialize"/> 幂等）。</summary>
    private static bool _initialized;

    // ---------------------------------------------------------------- 生命周期

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_DestroyLibrary();

    /// <summary>
    /// 初始化 PDFium。<b>幂等</b>，且必须在任何其他调用之前。
    /// <para>
    /// 只在<b>有 PDF 被打开过</b>时才付出这个代价：加载 <c>libpdfium.so</c> / <c>pdfium.dll</c>
    /// 要几毫秒，而它对"用户这一辈子不开 PDF"是完全白付的。所以调用点在
    /// <c>PdfPageRasterizer</c> 第一次真正要光栅化的时候，不在程序启动时。
    /// </para>
    /// </summary>
    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        FPDF_InitLibrary();
    }

    // ---------------------------------------------------------------- 文档

    /// <summary>
    /// 打开一个文件。<paramref name="password"/> 传 <c>null</c> 表示"没有口令" ——
    /// 所以这里刻意用 <see cref="string"/> 而不是 <c>byte[]</c>：C# 侧只有 <c>string</c> 能表达
    /// 「空指针」与「空字符串」的区别，而这两者对 PDFium 是两件事（前者试无口令，后者试空口令）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>两个字符串参数都必须按 UTF-8 传</b>，不是按 ANSI。
    /// PDFium 的 C API 收 <c>const char*</c> 并<b>一律当 UTF-8</b>，
    /// 而 .NET 侧写 <c>CharSet.Ansi</c> + <c>LPStr</c> 会按<b>系统 ANSI 代码页</b>
    /// （简体中文的机器上是 GBK）编出去。
    /// </para>
    /// <para>
    /// <b>症状是"文件明明在，却打不开"</b>：纯 ASCII 路径正常，
    /// 而路径里只要有一个汉字（文件名、目录名、用户名）就变成乱码字节，
    /// PDFium 找不到文件，返回空句柄。实测（2026-09-27）：
    /// <c>paper.pdf</c> 打开成功 16 页，<c>测试文档.pdf</c> 报"打不开"。
    /// 而本应用的默认安装位置是 <c>%LOCALAPPDATA%</c>，用户把文件放在中文目录下
    /// 是常态，所以这不是"少数情况"。
    /// </para>
    /// <para>
    /// <b>报出来的错误码还会把人带偏</b>：走 <c>FPDF_LoadDocument</c> 的失败路径
    /// 有时把 last error 留成上一次的值，于是窗口上出现"错误码 0 / 2"这种看不出指向的数。
    /// <b>那句话必须带上路径</b>，而排查的第一件事是看路径里有没有非 ASCII 字符。
    /// </para>
    /// </remarks>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern nint FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? filePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(nint document);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCount(nint document);

    /// <summary>PDFium 全局最后错误码。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint FPDF_GetLastError();

    // ---------------------------------------------------------------- 页

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FPDF_LoadPage(nint document, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(nint page);

    /// <summary>页宽（单位：<b>点</b>，1 pt = 1/72 英寸）。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double FPDF_GetPageWidth(nint page);

    /// <summary>页高（单位：点）。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double FPDF_GetPageHeight(nint page);

    // ---------------------------------------------------------------- 位图

    /// <summary>BGRA8888，每通道 8 位。<b>选它是因为它与本项目喂 <c>BitmapImage.FromPixels</c> 的格式一致</b>。</summary>
    internal const int BitmapFormatBgra = 4;

    /// <summary>只给最小复现用的直通口：绕开本类的句柄包装，直奔那三个原生调用。</summary>
    internal static class PdfiumNativeForProbe
    {
        private const int Bgra = 4;
        private const string Lib = "pdfium";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

        // 第二个参数是**页句柄**（FPDF_LoadPage 的返回值），不是页号 ——
        // 理由与后果见外层 FPDF_RenderPageBitmap 上那段。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);


        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_Destroy(nint bitmap);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint FPDF_LoadPage(nint document, int pageIndex);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(nint page);

        internal static nint LoadPage(nint document, int pageIndex) => FPDF_LoadPage(document, pageIndex);

        internal static void ClosePage(nint page) => FPDF_ClosePage(page);

        // FPDF_RenderPageBitmapWithMatrix：同一个渲染内核的另一个入口。
        // 拿来当 FPDF_RenderPageBitmap 的对照 —— 若它也不崩，崩点就在那个旧入口的包装里。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmapWithMatrix(nint bitmap, nint page, float[] matrix, int flags);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

        internal static void FillWhite(nint bitmap, int width, int height) =>
            FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);

        internal static void FillBlack(nint bitmap, int width, int height) =>
            FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFF000000);

        /// <summary>把位图整块拷回托管数组（用来"数一数到底画上了多少"）。</summary>
        internal static byte[] ReadBack(nint buffer, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return bytes;
        }

        /// <summary><c>FPDFBitmap_Create</c>：让 <b>PDFium 自己</b>分配缓冲。</summary>
        /// <remarks>
        /// 与 <c>FPDFBitmap_CreateEx</c> 的区别正好是"缓冲归谁"：那个把我们的缓冲
        /// 交给 PDFium，这个反过来。所以它是"外部缓冲这条路有没有问题"的对照实验。
        /// </remarks>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint FPDFBitmap_Create(int width, int height, int format);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint FPDFBitmap_GetBuffer(nint bitmap);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetStride(nint bitmap);

        internal static nint CreateOwnBufferBitmap(int width, int height) =>
            FPDFBitmap_Create(width, height, Bgra);

        /// <summary>同上，但格式由调用方给。</summary>
        internal static nint CreateOwnBufferBitmapOfFormat(int width, int height, int format) =>
            FPDFBitmap_Create(width, height, format);

        /// <summary>同上，但用 <c>FPDFBitmap_BGR</c>（3 字节/像素）。</summary>
        /// <remarks>
        /// 存在的理由是量出来的：这份库里 <c>FPDFBitmap_BGRx(3)</c> 与 <c>FPDFBitmap_BGRA(4)</c>
        /// <b>静默不渲染</b>（零墨量、不报错、不崩），而 <c>Gray(1)</c> 与 <c>BGR(2)</c> 正常出图。
        /// 代价是每行按 3 字节/像素走，读回与 stride 都不能再按 4 算。
        /// </remarks>
        internal static nint CreateOwnBufferBitmapBgr(int width, int height) =>
            FPDFBitmap_Create(width, height, Bgr);

        internal const int Bgr = 2;

        internal static nint GetBuffer(nint bitmap) => FPDFBitmap_GetBuffer(bitmap);

        internal static int GetStride(nint bitmap) => FPDFBitmap_GetStride(bitmap);

        /// <summary>当前进程里真正加载的是哪一份 <c>pdfium</c>（不是"我们以为的那份"）。</summary>
        /// <remarks>
        /// <c>DllImport("pdfium")</c> 只给一个名字，最终由 <c>LoadLibrary</c> 决定 ——
        /// 而它除进程目录外还搜 PATH。这台机器上 <c>C:\git</c> 下有好几个 PDF 相关工程，
        /// 各自可能带一份 pdfium。所以"页数对得上"并不能证明加载的是我们要的那份。
        /// </remarks>
        internal static string LoadedModulePath()
        {
            var h = GetModuleHandle("pdfium.dll");
            if (h == 0) return "(GetModuleHandle 没找到 pdfium.dll)";
            var sb = new System.Text.StringBuilder(1024);
            var n = GetModuleFileName(h, sb, sb.Capacity);
            return n == 0 ? "(GetModuleFileName 失败)" : sb.ToString(0, n);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandle(string? moduleName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetModuleFileName(nint hModule, System.Text.StringBuilder lpFilename, int nSize);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageWidth(nint page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageHeight(nint page);

        internal static (double Width, double Height) PageSizeOf(nint page) =>
            (FPDF_GetPageWidth(page), FPDF_GetPageHeight(page));

        internal static uint LastError() => FPDF_GetLastError();

        // ── 文本层：用来分清"文档读不动"与"只有光栅化死了" ──────────────────
        // 两者在别的探针里长得一模一样（都是"什么也没发生"），而这一组
        // 走的是完全不同的代码路径（解析 + 字体映射，不碰光栅化内核）。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern nint FPDFText_LoadPage(nint page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFText_ClosePage(nint textPage);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_CountChars(nint textPage);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_GetText(nint textPage, int startIndex, int count, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder buffer);

        internal static int TextCharCount(nint page)
        {
            var tp = FPDFText_LoadPage(page);
            if (tp == 0) return -1;
            try { return FPDFText_CountChars(tp); }
            finally { FPDFText_ClosePage(tp); }
        }

        internal static string TextOf(nint page, int maxChars)
        {
            var tp = FPDFText_LoadPage(page);
            if (tp == 0) return "(FPDFText_LoadPage 返回空)";
            try
            {
                var sb = new System.Text.StringBuilder(maxChars + 1);
                FPDFText_GetText(tp, 0, maxChars, sb);
                return sb.ToString();
            }
            finally { FPDFText_ClosePage(tp); }
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDF_GetLastError();

        internal static void RenderPageWithMatrix(nint bitmap, nint page, int flags, float scale = 1f)
        {
            // 缩放 scale、位移 0 的仿射矩阵（PDFium 的 FS_MATRIX 是 6 个 float：
            // x' = a·x + c·y + e，y' = b·x + d·y + f）。
            var matrix = new[] { scale, 0f, 0f, scale, 0f, 0f };
            FPDF_RenderPageBitmapWithMatrix(bitmap, page, matrix, flags);
        }

        /// <summary>同一个入口，但按 <b>3×3 九个 float</b> 传矩阵。</summary>
        /// <para>
        /// 存在的理由：6 与 9 只差"PDFium 到底读几个 float"，而读少了的后果不是报错，
        /// 是<b>越界读到我方数组后面的栈垃圾</b>、变换成随机值、**内容全被映射到图外** ——
        /// 表现是"渲染不崩，但整页空白"。这两种布局的差别只有这一个数，
        /// 所以两个都试一遍，印出各自的非白像素数。
        /// </para>
        /// </summary>
        [DllImport(Lib, EntryPoint = "FPDF_RenderPageBitmapWithMatrix", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmapWithMatrix9(nint bitmap, nint page, float[] matrix, int flags);

        internal static void RenderPageWithMatrix9(nint bitmap, nint page, int flags, float scale = 1f)
        {
            // 3×3 行主序：a b 0 / c d 0 / e f 1
            var matrix = new[]
            {
                scale, 0f, 0f,
                0f, scale, 0f,
                0f, 0f, 1f,
            };
            FPDF_RenderPageBitmapWithMatrix9(bitmap, page, matrix, flags);
        }


        internal static nint CreateBitmap(int width, int height, nint buffer, int stride) =>
            FPDFBitmap_CreateEx(width, height, Bgra, buffer, stride);

        /// <summary>同上，但格式由调用方给（用来扫 Gray/BGR/BGRx/BGRA 哪一个才被渲染认）。</summary>
        internal static nint CreateBitmapOfFormat(int width, int height, nint buffer, int stride, int format) =>
            FPDFBitmap_CreateEx(width, height, format, buffer, stride);


        internal static void RenderPage(nint bitmap, nint pageHandle, int width, int height) =>
            FPDF_RenderPageBitmap(bitmap, pageHandle, 0, 0, width, height, 0, 0);


        internal static void DestroyBitmap(nint bitmap) => FPDFBitmap_Destroy(bitmap);
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(nint bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    /// <summary>
    /// 渲染一页。**第二个参数是页句柄（<c>FPDF_LoadPage</c> 的返回值），不是页号。**
    /// <para>
    /// 这是本项目踩过的<b>最贵的一个错</b>，值得把它的两种症状都写下来：
    /// </para>
    /// <list type="number">
    /// <item>PDFium 的实现第一行是 <c>if (bitmap == NULL || page == NULL) return;</c>。
    /// 所以<b>页号 0 传进来正好是空指针 → 函数立刻返回</b>：不崩、不报错、
    /// <b>一格像素都不画</b>。看上去"第 1 页渲染成功"，其实什么也没发生
    /// （探针里那次"612×792 渲染耗时 1 ms"就是量了一次提前返回）。</item>
    /// <item><b>页号 ≥ 1 传进来被当成指针 <c>0x1</c>、<c>0x2</c>…</b>，
    /// 于是解引用一个几乎为空的地址 → <b>0xC0000005 硬崩</b>。
    /// 托管层的 <c>try/catch</c> 与 <c>Task</c> 异常<b>都接不到</b>，整个进程走。</item>
    /// </list>
    /// <para>
    /// 症状凑在一起极具误导性：<b>"第 1 页好的、第 2 页起崩"</b>，
    /// 看起来像"这份库渲染多页有缺陷"，于是换了六七个版本的原生库（全部一样），
    /// 还差点归咎于并发、文档生命周期、渲染标志位。<b>全部是这条声明的问题。</b>
    /// </para>
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    /// <summary>
    /// 渲染标志：一律<b>什么都不加</b>（0）。
    /// <para>
    /// 我们要的是页面**内容本身** —— PDF 的批注层是另一回事，而本应用自己的批注
    /// 画在它上面（引擎那份墨迹），所以 PDF 自带的批注不需要。
    /// </para>
    /// <para>
    /// <b>这里曾经挂着一个"被 0xC0000005 教出来的"结论，是错的，现在撤掉。</b>
    /// 当时的说法是：<c>RenderNoAnnotations = 0x01</c> 与 PDFium 的
    /// <c>FPDF_ANNOT = 0x01</c> 名字相反，于是"每一页都在被要求渲染批注"，
    /// 翻到带批注的那一页就崩。<b>常量本身没错</b>（<c>0x01</c> 确实是
    /// <c>FPDF_ANNOT</c>，"不传它"也是我们要的），但<b>它从来不是崩因</b> ——
    /// 那时传进去的第二个参数是页号，整个函数在第一行就 return 了，
    /// 标志位压根没被读到。改成传句柄之后，<c>flags = 0</c> 干净地渲出 16 页。
    /// </para>
    /// <para>
    /// 真正该留下来的教训只有一条，而且与标志位无关：
    /// <b>"换了六七个版本的库、换了缓冲归属、换了渲染入口，症状一字不变"</b>
    /// 是"问题在我们这边"的最强信号 —— 每换一样都完全一样，就该怀疑自己那几行，
    /// 而不是继续换外部依赖。详见 <see cref="FPDF_RenderPageBitmap"/> 上那段。
    /// </para>
    /// </summary>
    internal const int RenderContentOnly = 0x00;

    /// <summary><c>FPDF_ANNOT</c>：把 PDF **自带的**批注也画进去。<b>我们不传它</b>。</summary>
    internal const int RenderAnnotations = 0x01;

    /// <summary><c>FPDF_LCD_TEXT</c>：亚像素渲染文字。关掉它更慢但更清楚，且不需要彩色字体路径。</summary>
    internal const int RenderLcdText = 0x02;

    /// <summary><c>FPDF_PRINTING = 0x8000</c>：打印质量（注意<b>不是</b> 0x02）。</summary>
    internal const int RenderPrinting = 0x8000;

    /// <summary><c>FPDF_NO_SMOOTHTEXT = 0x1000</c>。</summary>
    internal const int RenderNoSmoothText = 0x1000;

    /// <summary><c>FPDF_NO_SMOOTHIMAGE = 0x2000</c>。</summary>
    internal const int RenderNoSmoothImage = 0x2000;

    /// <summary><c>FPDF_NO_SMOOTHING = 0x4000</c>（图与字都不平滑）。</summary>
    internal const int RenderNoSmoothing = 0x4000;

    // ---------------------------------------------------------------- 包装类型

    /// <summary>一个已打开的 PDF 文档。<b>非线程安全</b>，整条链都在一把锁后面。</summary>
    internal sealed class DocumentHandle : IDisposable
    {
        internal nint Handle { get; }
        private bool _disposed;

        internal DocumentHandle(string path)
        {
            Handle = FPDF_LoadDocument(path, null);
            if (Handle == 0)
            {
                throw new PdfNativeException(
                    $"PDFium 打不开这个文件（错误码 {FPDF_GetLastError()}）：{path}");
            }
        }

        internal int PageCount => FPDF_GetPageCount(Handle);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FPDF_CloseDocument(Handle);
        }
    }

    /// <summary>一页。<b>用完立刻释放</b> —— 一份几十页的大文档同时开着几十个页对象会很浪费。</summary>
    internal sealed class PageHandle : IDisposable
    {
        internal nint Handle { get; }
        private bool _disposed;

        internal PageHandle(nint document, int pageIndex)
        {
            Handle = FPDF_LoadPage(document, pageIndex);
            if (Handle == 0)
            {
                throw new PdfNativeException($"PDFium 读不了第 {pageIndex + 1} 页。");
            }
        }

        /// <summary>页尺寸，单位<b>点</b>（1/72 英寸）。</summary>
        internal (double WidthPt, double HeightPt) SizePoints =>
            (FPDF_GetPageWidth(Handle), FPDF_GetPageHeight(Handle));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FPDF_ClosePage(Handle);
        }
    }

    /// <summary>
    /// 一块可写的 BGRA 位图，包住我们自己分配的 unmanaged 缓冲。
    /// <para>
    /// <b>必须自己分配</b>：PDFium 按 <c>stride</c> 写入，托管数组不保证连续也不保证对齐，
    /// 所以走 unmanaged 内存，渲染完 <c>CopyToManaged</c> 出来再释放。
    /// </para>
    /// <para>
    /// <b>用 <see cref="Marshal"/> 而不是 <c>NativeMemory</c></b>：后者的
    /// <c>Alloc</c>/<c>Copy</c> 签名是 <c>void*</c>，不置 <c>AllowUnsafeBlocks</c> 就编不过 ——
    /// 而本项目<b>刻意不开</b>那个闸门（同 SYSLIB1062 那条：用 <c>DllImport</c> 而不是
    /// <c>LibraryImport</c>，就是为了不为六个入口把不安全代码打开）。<c>Marshal.AllocHGlobal</c>
    /// 直接给 <see cref="nint"/>，全程不需要 unsafe 上下文。
    /// </para>
    /// </summary>
    internal sealed class BitmapHandle : IDisposable
    {
        private nint _buffer;
        private nint _bitmap;
        private bool _disposed;

        internal int Width { get; }
        internal int Height { get; }

        /// <summary>每行字节数（<c>width × 4</c>，BGRA 紧密排列）。</summary>
        internal int Stride => Width * 4;

        internal BitmapHandle(int width, int height)
        {
            Width = width;
            Height = height;
            _buffer = Marshal.AllocHGlobal(checked(Stride * height));
            _bitmap = FPDFBitmap_CreateEx(width, height, BitmapFormatBgra, _buffer, Stride);
            if (_bitmap == 0)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
                throw new PdfNativeException($"PDFium 建不出 {width}×{height} 的位图。");
            }

            // 不填的话 PDFium 只画有内容的区域，其余是**未初始化内存** ——
            // 直接当像素拷走就是一团噪点，而那正好是"空白页显示成乱码"的成因。
            FPDFBitmap_FillRect(_bitmap, 0, 0, width, height, 0xFFFFFFFF);
        }

    /// <summary>
    /// 把某一页画进来。<paramref name="flags"/> 见 <see cref="RenderAnnotations"/> 等。
    /// </summary>
    /// <remarks>
    /// <paramref name="page"/> 是 <see cref="PageHandle.Handle"/>（<c>FPDF_LoadPage</c> 的返回值），
    /// **不是页号** —— 见 <see cref="FPDF_RenderPageBitmap"/> 上那段关于"页号 0 = 空指针"的说明。
    /// </remarks>
    internal void RenderPage(nint page, int flags) =>
        FPDF_RenderPageBitmap(_bitmap, page, 0, 0, Width, Height, 0, flags);


        /// <summary>拷成托管 BGRA 数组（<c>BitmapImage.FromPixels</c> 的输入）。</summary>
        internal byte[] CopyToManaged()
        {
            var bytes = new byte[Stride * Height];
            Marshal.Copy(_buffer, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_bitmap != 0) FPDFBitmap_Destroy(_bitmap);
            if (_buffer != 0) Marshal.FreeHGlobal(_buffer);
            _bitmap = 0;
            _buffer = 0;
        }
    }
}

/// <summary>PDFium 调用失败。带一句人话，而不是裸错误码。</summary>
internal sealed class PdfNativeException : Exception
{
    internal PdfNativeException(string message) : base(message) { }
}
