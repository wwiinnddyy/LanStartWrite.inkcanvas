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

        return RunFull(path);
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

            PdfiumNative.PdfiumNativeForProbe.RenderPage(bitmap, page, width, height);
            Console.WriteLine($"[min] render page {page} ok");

            if (loaded != 0) PdfiumNative.PdfiumNativeForProbe.ClosePage(loaded);
            PdfiumNative.PdfiumNativeForProbe.DestroyBitmap(bitmap);
            Marshal.FreeHGlobal(buffer);
        }

        document.Dispose();
        Console.WriteLine("[min] all ok");
        return 0;
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
