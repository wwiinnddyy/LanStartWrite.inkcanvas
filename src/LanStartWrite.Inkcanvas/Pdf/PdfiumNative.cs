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
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern nint FPDF_LoadDocument([MarshalAs(UnmanagedType.LPStr)] string? filePath, [MarshalAs(UnmanagedType.LPStr)] string? password);

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

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmap(nint bitmap, int pageIndex, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

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

        internal static void RenderPageWithMatrix(nint bitmap, nint page, int flags)
        {
            // 缩放 1、位移 0 的单位矩阵（PDFium 用的是 3x2 仿射的前 4 个数）。
            var matrix = new[] { 1f, 0f, 0f, 1f, 0f, 0f };
            FPDF_RenderPageBitmapWithMatrix(bitmap, page, matrix, flags);
        }

        internal static nint CreateBitmap(int width, int height, nint buffer, int stride) =>
            FPDFBitmap_CreateEx(width, height, Bgra, buffer, stride);

        internal static void RenderPage(nint bitmap, int pageIndex, int width, int height) =>
            FPDF_RenderPageBitmap(bitmap, pageIndex, 0, 0, width, height, 0, 0);

        internal static void DestroyBitmap(nint bitmap) => FPDFBitmap_Destroy(bitmap);
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(nint bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(
        nint bitmap,
        int pageIndex,
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
    /// <b>这一条是被一次 0xC0000005 教出来的</b>：这里原先写着
    /// <c>RenderNoAnnotations = 0x01</c>，而 PDFium 的 <c>0x01</c> 是
    /// <c>FPDF_ANNOT</c>，含义正好<b>相反</b> —— 于是每一页都在被要求渲染批注。
    /// 首页没有批注所以看不出来，翻到第 2 页（那份 PDF 从第 2 页起带批注）
    /// 当场 <b>0xC0000005 硬崩</b>：托管层的 try/catch 接不到，整个进程走。
    /// </para>
    /// <para>
    /// 教训：**标志位不能凭名字推**，而"名字写反"在这里是彻底静默的 ——
    /// 它不会少画什么，只会在某一页上崩，而那一页与出错的那行代码隔着十万八千里。
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

        /// <summary>把某一页画进来。<paramref name="flags"/> 见 <see cref="RenderNoAnnotations"/> 等。</summary>
        internal void RenderPage(int pageIndex, int flags) =>
            FPDF_RenderPageBitmap(_bitmap, pageIndex, 0, 0, Width, Height, 0, flags);

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
