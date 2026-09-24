using System.Runtime.InteropServices;
using Jalium.UI;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 把屏幕<b>截一张图</b>，给冻结模式当画布底。走 GDI 的 <c>BitBlt</c>，是本文件里唯一一处图形互操作。
/// <para>
/// <b>为什么不用 Jalium 自己的渲染路径</b>：要截的是<b>别的应用</b>的像素，Jalium 的
/// <c>RenderTargetBitmap</c> 只能渲染它自己的视觉树。屏幕 DC 是唯一拿到"屏幕上现在长什么样"的入口。
/// </para>
/// <para>
/// <b>截的范围是"参考窗口盖住的那块屏幕"</b>（<c>GetWindowRect</c>），不是整个虚拟桌面：
/// 画布只盖一块屏幕，多显示器下截整张桌面再拉伸会错位。按窗口矩形截，截出来的像素尺寸天然等于
/// 窗口的物理尺寸，配上算出来的 DPI 就正好 1:1 铺满。
/// </para>
/// </summary>
internal static class ScreenCapture
{
    private const int Srccopy = 0x00CC0020;

    /// <summary>把分层窗口也画进去（别的应用的亚克力 / 半透明窗口，不带上它会是黑的）。</summary>
    private const uint Captureblt = 0x40000000;

    private const uint DibRgbColors = 0;
    private const int BiRgb = 0;

    private const uint WdaNone = 0x00000000;

    /// <summary>"这个窗口不要出现在截屏里"（Windows 10 2004+）。</summary>
    private const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>
    /// 截 <paramref name="reference"/> 盖住的那块屏幕。
    /// <para>
    /// <paramref name="excludeFromCapture"/> 里的窗口在截取期间会被外壳排除掉 ——
    /// <b>这是为了不让批注栏自己被截进去</b>：它是常驻的，截进去之后一旦被挪走，
    /// 原位置就会留一块"旧批注栏"的鬼影，而画布上是擦不掉的（那是底图）。
    /// </para>
    /// <para>截不到（句柄没建出来、矩形为空、DIB 分配失败）返回 <c>null</c>；调用方按"这次没有底图"处理。</para>
    /// </summary>
    internal static BitmapSource? CaptureBehind(Window reference, params Window?[] excludeFromCapture)
    {
        // 截屏是 Win32 的事（GetWindowRect + BitBlt）；Linux 上没有这一层，冻结模式降级为"没有底图"。
        if (!OperatingSystem.IsWindows()) return null;

        var rect = NativeWindowZOrder.WindowRect(reference.Handle);
        if (rect is null) return null;

        var (left, top, right, bottom) = rect.Value;
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0) return null;

        var excluded = ExcludeFromCapture(excludeFromCapture);
        try
        {
            return CaptureRegion(left, top, width, height, reference);
        }
        finally
        {
            foreach (var excludedHandle in excluded) SetWindowDisplayAffinity(excludedHandle, WdaNone);
        }
    }

    private static BitmapSource? CaptureRegion(int x, int y, int width, int height, Window reference)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return null;

            // 负高度 = 自上而下的 DIB：行 0 就是屏幕最上面那一行，与图像的内存布局一致，
            // 否则整张图会上下颠倒（而且颠倒这件事在纯色桌面上看不出来）。
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = 40,
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };

            bitmap = CreateDIBSection(screenDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, Srccopy | Captureblt)) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // GDI 的 BitBlt 只写 BGR 三个字节，alpha 字节留着上一次那块内存里的随便什么值（多半是 0）。
            // 用 Bgra32 就必须自己补成不透明：不补的话整张图是全透明的 ——
            // 这个坑不报错，只是画面空白，很难往"截图"上想。
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            // DPI 按"截到的像素数 ÷ 窗口的 DIP 尺寸"算出来，于是这张图的自然尺寸正好等于窗口尺寸。
            // 这样无论外壳把 ImageBrush 当"拉伸"还是"原样"处理，铺上去都是 1:1。
            //
            // 用 ActualWidth 而不是 Width：画布是最大化的，而 <c>Window.Width</c> 在最大化窗口上
            // 报的是"还原尺寸"而不是现在的尺寸（WPF 一路都是这个脾气，Jalium 也照抄了）——
            // 拿它算出来的 DPI 会偏得离谱，而表现只是"底图被放大/缩小了一块"，不报错。
            var dpiX = 96.0 * width / Math.Max(1.0, reference.ActualWidth);
            var dpiY = 96.0 * height / Math.Max(1.0, reference.ActualHeight);

            return BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 让这些窗口别进截屏。返回<b>真的设上了</b>的那些句柄，调用方据此还原。
    /// <para>
    /// 不做事后兜底（比如"设不上就把窗口藏起来再截"）：那会让每次进入画布都闪一下批注栏，
    /// 而这里只影响观感。本应用本来就要求 Windows 11（Segoe Fluent Icons、Win11 圆角），
    /// 而这个接口在 Windows 10 2004 之后就有。
    /// </para>
    /// </summary>
    private static List<IntPtr> ExcludeFromCapture(Window?[] windows)
    {
        var applied = new List<IntPtr>(windows.Length);
        foreach (var window in windows)
        {
            if (window is null) continue;
            if (window.Visibility != Visibility.Visible) continue;

            var handle = window.Handle;
            if (handle == IntPtr.Zero) continue;
            if (SetWindowDisplayAffinity(handle, WdaExcludeFromCapture)) applied.Add(handle);
        }

        return applied;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    /// <summary>BITMAPINFO = 头 + 一张单色表的头一格。<c>BI_RGB</c> 的 32 位图用不到那张表，
    /// 但结构体的<b>大小</b>得对（<c>CreateDIBSection</c> 会读头里的 <c>biSize</c>，也认整体尺寸）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hDc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hDcDest, int x, int y, int width, int height, IntPtr hDcSrc, int xSrc, int ySrc, uint rop);
}
