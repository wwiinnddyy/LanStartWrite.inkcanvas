using System.Diagnostics;
using System.Runtime.InteropServices;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Interop;
using Jalium.UI.Markup;
// 与主应用的 PdfViewerWindow.jalxaml.cs 用同一组 using：
//   RenderContext/RenderBackend/RenderingEngine/ThemeLoader 在 Jalium.UI.Interop / .Markup，
//   DispatcherTimer 在 Jalium.UI.Threading。
//   **少一个 using 的症状是"控件构造之前就炸"**，而那恰好是排版表的生效时刻 ——
//   与主应用里 ThemeLoader 必须排在任何解析之前是同一条理由。
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas.Tools;

internal static class CameraUiProbe
{
    internal static int Run()
    {
        var renderContext = RenderContext.GetOrCreateCurrent(RenderBackend.Auto);
        renderContext.DefaultRenderingEngine = RenderingEngine.Impeller;
        ThemeLoader.Initialize();
        var app = new Application();

        var window = new Window
        {
            Title = "CameraProbe",
            Width = 1280,
            Height = 800,
        };
        var live = new Image { Stretch = Stretch.Uniform };
        var status = new TextBlock { Margin = new Thickness(8), FontSize = 15 };
        var host = new Grid();
        host.Children.Add(live);
        host.Children.Add(status);
        window.Content = host;

        var view = new CameraView { Stretch = Stretch.Uniform };
        // **static** —— 反射 dump 当时没区分 instance/static，写成实例调用是 CS0176。
        // 这一点对实现有直接影响：枚举设备不���要先把控件建出来，
        // 所以"有没有摄像头"可以在进窗口之前就问清。
        Console.WriteLine($"IsCaptureSupported = {CameraView.IsCaptureSupported}");

        var devices = CameraView.EnumerateDevices();
        Console.WriteLine($"devices = {devices.Count}");
        if (devices.Count == 0) { Console.WriteLine("没有设备，量不了"); return 1; }

        view.Source = devices[0];
        view.RequestedWidth = 1280;
        view.RequestedHeight = 720;
        view.RequestedFps = 30;

        var arrived = 0;
        var swapped = 0;
        var swap = new List<double>();
        var firstArrivalMs = -1.0;
        BitmapImage? lastFrame = null;
        var stamp = Stopwatch.StartNew();

        view.CameraFrameArrived += (_, _) =>
        {
            arrived++;
            if (firstArrivalMs < 0) firstArrivalMs = stamp.Elapsed.TotalMilliseconds;
            if (view.CurrentFrame is not BitmapImage frame) return;

            // 实现要走的就是这一步：把池化帧换到 Image.Source 上。
            // 顺带量它有多贵 —— 那是"能不能撑住 30fps"唯一有意义的数字。
            var sw = Stopwatch.StartNew();
            live.Source = frame;
            swapped++;
            swap.Add(sw.Elapsed.TotalMilliseconds);

            // 上一张必须释放：BitmapImage 是 IDisposable/IReclaimableResource，
            // 而 ImageSource 基类**没有** Dispose（只有 BitmapImage 那一层有）。
            // 30fps 下一帧一换而不释放就是每秒 30 次 GPU 资源堆积。
            lastFrame?.Dispose();
            lastFrame = frame;
        };

        window.Shown += (_, _) =>
        {
            view.Start();
            var closer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            closer.Tick += (_, _) =>
            {
                closer.Stop();
                Report(view, live, status, stamp, arrived, swapped, swap, firstArrivalMs);
                SaveSelfCapture(window, Path.Combine(Path.GetTempPath(), "cameraprobe-ui.png"));
                view.Stop();
                app.Shutdown();
            };
            closer.Start();
        };

        app.MainWindow = window;
        window.Show();
        app.Run();
        return 0;
    }

    private static void Report(
        CameraView view, Image live, TextBlock status,
        Stopwatch stamp, int arrived, int swapped, List<double> swap, double firstArrivalMs)
    {
        var seconds = Math.Max(0.001, stamp.Elapsed.TotalSeconds);
        Console.WriteLine();
        Console.WriteLine("== 结果 ==");
        Console.WriteLine($"  首帧到达    : {(firstArrivalMs < 0 ? "**没有帧到达**" : $"{firstArrivalMs:0} ms")}");
        Console.WriteLine($"  到达帧数    : {arrived}（{arrived / seconds:0.0} fps）");
        Console.WriteLine($"  实际换 Source: {swapped}");
        Console.WriteLine($"  LastError   : {(view.LastError is { } e ? $"{e.GetType().Name}: {e.Message}" : "（无）")}");
        Console.WriteLine($"  Image.Source: {(live.Source is null ? "null" : $"{live.Source.GetType().Name} {live.Source.GetType().GetProperty("PixelWidth")?.GetValue(live.Source)}×{live.Source.GetType().GetProperty("PixelHeight")?.GetValue(live.Source)}")}");
        if (swap.Count > 0)
        {
            var s = swap.OrderBy(x => x).ToArray();
            Console.WriteLine($"  换 Source   : 中位 {s[s.Length / 2]:0.00} ms  最大 {s[^1]:0.00} ms  " +
                              $"→ 单这一步的上限约 {1000.0 / Math.Max(0.01, s[s.Length / 2]):0.0} fps");
        }

        // 状态条留在窗口上：截下来的图里能看到它，那比任何控制台输出都更像"现场"。
        status.Text = $"到达 {arrived} 帧（{arrived / seconds:0.0} fps）· 换 {swapped} 次 Source";
    }

    /// <summary>用 GDI 把自己这个窗口截下来，存成 PNG。</summary>
    /// <remarks>
    /// 抄主应用 <c>ScreenCapture.cs</c> 的做法（GDI + <b>负</b>高度 top-down DIB）而不是自己另写一套：
    /// 正的 DIB 高度会把图上下翻转，而在纯色背景上那几乎看不出来 ——
    /// 那是"看着挺像那么回事"的典型。
    /// </remarks>
    private static void SaveSelfCapture(Window window, string path)
    {
        // **GetWindowRect 成功时返回非零**（Win32 老规矩，TRUE = 成功）。
        // 我一开始写反成 `!= 0` 当失败，于是自截图永远不落盘 —— 而症状只是
        // "少了一张图"，看起来像"截图功能没实现"。
        if (GetWindowRect(window.Handle, out var r) == 0) { Console.WriteLine("  取不到自己的窗口矩形"); return; }
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) { Console.WriteLine("  窗口矩形为空"); return; }

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32, Compression = 0 },
        };
        // CreateDIBSection 返回的是 DIB **句柄**（要 SelectObject 进去、DeleteObject 掉），
        // 像素指针是 out 参数。两个都要 —— 只留一个就是"截出来一张空图"。
        var dib = CreateDIBSection(screen, ref info, 0, out var bits);
        var old = SelectObject(mem, dib);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SrcCopy);
        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);

        var px = new byte[w * h * 4];
        Marshal.Copy(bits, px, 0, px.Length);
        File.WriteAllBytes(path, Png.EncodeBgra(px, w, h));

        // 报一下非黑像素占比：一整张全黑 = 什么都没画，
        // 而"没报错"是那类坏最常见的伪装。
        long notBlack = 0;
        for (var i = 0; i < px.Length; i += 4)
        {
            if (px[i] > 24 || px[i + 1] > 24 || px[i + 2] > 24) notBlack++;
        }
        Console.WriteLine($"  自截图 {w}×{h} → {path}（{new FileInfo(path).Length / 1024.0:N0} KB，" +
                          $"非黑像素 {notBlack * 100.0 / (w * h):0.0}%）");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32")] private static extern int GetWindowRect(nint h, out Rect r);
    [DllImport("user32")] private static extern nint GetDC(nint h);
    [DllImport("user32")] private static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int rop);
    [DllImport("gdi32")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits);
    private const int SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height, Planes, BitCount, Compression, SizeImage,
            XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
}
