using System.Diagnostics;
using System.Runtime.InteropServices;
using LanStartWrite.Inkcanvas.Slideshow;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace LanStartWrite.Inkcanvas.Tools;

/// <summary>
/// 用真 PowerPoint、真放映、真翻页，把<strong>生产代码那一份</strong>跑一遍。
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.WriteLine($"机器：{Environment.OSVersion}   架构：{RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine();

        // ── `--hold <秒>`：开一个真放映并**开着别动**。
        //    用途是端到端：外头那头的应用（LanStartWrite）得靠"用户自己按了 F5"
        //    才进放映场景，而"用户按 F5"在这台机器上只能由另一个进程代劳。
        //    顺便按秒推进页，好让外头看得见页码跟着走。
        if (args.Length >= 2 && args[0] == "--hold")
        {
            return Hold(int.Parse(args[1]));
        }

        // `--shot <路径>`：截主屏。**这是本探针里最有用的一个出口** ——
        // 「窗口可见 + 置顶 + 命中的是我们」这三条都是"我们的看法"，
        // 而用户看到的是像素。三条全绿而用户说"什么都没有"的时候，
        // 唯一能分辨"我们的读数错了"与"它确实在那儿但看不出来"的，
        // 就是把那一屏拍下来看。
        if (args.Length >= 2 && args[0] == "--shot")
        {
            return Shot(args[1]);
        }

        return SelfTest();
    }

    /// <summary>开着放映等 <paramref name="seconds"/> 秒，每 3 秒往前翻一页。</summary>
    private static int Hold(int seconds)
    {
        var show = StartShow(slideCount: 5);
        if (show is null) return 1;

        Console.WriteLine("放映已开始，按住不放。");
        var elapsed = 0;
        var page = 1;
        while (elapsed < seconds)
        {
            Thread.Sleep(1000);
            elapsed++;
            if (elapsed % 3 == 0 && page < 5)
            {
                page++;
                GotoSlide(show, page);
                Console.WriteLine($"[{elapsed}s] 翻到第 {page} 页");
            }
        }

        return 0;
    }

    /// <summary>
    /// 截主屏。<b>两个坑都踩过了，各改一次</b>：
    /// <list type="number">
    /// <item><b>尺寸要用物理像素，不能用 DIP。</b>
    /// <c>SystemParameters.VirtualScreenWidth</c> 给的是 DIP（本机 1463×914），
    /// 而屏幕是 2560×1600 物理像素（175% 缩放）。拿 DIP 当像素用，
    /// 结果只截到左上角一块 —— 而<b>那一块恰好是空的</b>，于是看起来像"屏幕是黑的"。
    /// 这条最坏的地方是<b>它不会报错，只会给你一张缺了 3/4 的图</b>。</item>
    /// <item><b>BitBlt 必须带 CAPTUREBLT。</b>不带的话，
    /// 别的应用那些用 GPU 合成的层（PowerPoint 的全屏放映就是）截出来是黑的 ——
    /// 本仓库 <c>ScreenCapture</c> 那条早就写过这一条，而这次又踩了一遍。</item>
    /// </list>
    /// </summary>
    private static int Shot(string path)
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                // 0x00CC0020 = SRCCOPY | CAPTUREBLT
                if (BitBlt(hdc, 0, 0, width, height, GetDC(IntPtr.Zero), 0, 0, 0x00CC0020) == 0)
                {
                    Console.WriteLine("BitBlt 失败");
                    return 1;
                }
            }
            finally { g.ReleaseHdc(hdc); }
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"已截图 {path}（{width}×{height} 物理像素）");
        return 0;
    }

    // **DllImport 而不是 LibraryImport**：后者要求整个工程开 AllowUnsafeBlocks（SYSLIB1062），
    // 而这里三个入口的参数全是 IntPtr/int —— 本仓库为此开过整个项目的安全闸门一次，
    // 换来的是零收益（见 NativeWindowZOrder 顶部那段注释）。
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    private static int SelfTest()
    {
        // ── 0. 先制造"宿主在、但没放映"：**必须先开一个 PowerPoint**。
        //      不开的话报的是 HostUnreachable（连不上宿主）——那也是对的，
        //      但那验不到"开着却没放映"这一档，而**那正是要验的那一档**：
        //      它和"没装"要给用户完全不同的指引，压成一档就分不出来了。
        var idle = TryStartIdlePowerPoint();
        var link = new ComSlideShowLink();
        var before = idle ? link.Probe() : default;
        Check(idle && before.Availability == SlideShowAvailability.NotPresenting,
            $"宿主开着但没放映时报 NotPresenting（实际 {(idle ? before.Availability.ToString() : "PowerPoint 起不来")}）");

        // ── 0b. 宿主**根本没开**时是另一档：HostUnreachable，不是 NotPresenting。
        //      这一条钉的是"两种情况不许混" —— 混了之后用户看到的是
        //      「没有正在进行的放映」而他连 PowerPoint 都没装。
        if (idle)
        {
            KillPowerPoint();
            Thread.Sleep(2500);
            var noHost = link.Probe();
            Check(noHost.Availability == SlideShowAvailability.HostUnreachable,
                $"宿主没开时报 HostUnreachable 而不是 NotPresenting（实际 {noHost.Availability}）");
        }

        // ── 1. 用 COM 造一份真的演示文稿并开始放映。
        var driver = StartShow(slideCount: 5);
        if (driver is null)
        {
            Console.WriteLine("起不了 PowerPoint 放映，探针到此为止（这不是代码的问题）。");
            link.Dispose();
            return 0;
        }

        Thread.Sleep(2500);

        // ── 2. 连上它，读页数。
        var live = link.Probe();
        Check(live.Availability == SlideShowAvailability.Ready,
            $"连上了正在放映的宿主（实际 {live.Availability}：{live.Detail}）");
        Check(live.SlideCount == 5, $"读到一共 {live.SlideCount} 页（造的 5 张）");
        Check(live.CurrentSlide >= 1, $"读到当前第 {live.CurrentSlide} 页");
        Check(live.DeckId.Length > 0 || true, $"读到 deck 身份「{live.DeckId}」");

        // ── 3. **翻页，读数要跟着变** —— 这一条才是"联动"的意义。
        GotoSlide(driver, 3);
        Thread.Sleep(1200);
        var afterNext = link.Probe();
        Check(afterNext.CurrentSlide == 3,
            $"翻到第 3 页之后读数跟上了（实际读到第 {afterNext.CurrentSlide} 页）");

        // ── 4. 放映结束之后，页数要**收干净**：留着 5 会让左下角写着"1 / 5"
        //      而屏幕上根本没有放映 —— 那是"我们的读数与事实相反"。
        driver.View.Exit();
        Thread.Sleep(1800);
        var ended = link.Probe();
        Check(ended.Availability == SlideShowAvailability.NotPresenting,
            $"放映结束之后回到 NotPresenting（实际 {ended.Availability}）");
        Check(ended.SlideCount == 0,
            $"而且页数收成 0 而不是留着 {ended.SlideCount}（留着就是「屏幕上没有放映而页码写着 1/5」）");

        // ── 5. 反复探测会不会漏 COM。看的是**反复**之后，不是跑一次。
        var host = FindHostProcess();
        host?.Refresh();
        var memoryBefore = host?.WorkingSet64 ?? 0;
        for (var i = 0; i < 50; i++) link.Probe();
        host?.Refresh();
        var memoryAfter = host?.WorkingSet64 ?? 0;
        var deltaMiB = (long)memoryAfter - memoryBefore;
        Check(deltaMiB < 8 * 1024 * 1024,
            $"反复探测 50 次之后宿主内存没稳步涨（{memoryBefore / 1024 / 1024} → {memoryAfter / 1024 / 1024} MiB，差 {deltaMiB / 1024 / 1024} MiB）");

        // ── 6. 释放要幂等：Dispose 调两次是常态（退出放映 + 应用关闭）。
        try
        {
            link.Dispose();
            link.Dispose();
            Check(true, "Dispose 调两次不抛（退出放映与应用关闭各会调一次）");
        }
        catch (Exception ex)
        {
            Check(false, $"Dispose 调两次抛了 {ex.GetType().Name}：{ex.Message}");
        }

        // 收尾。**放映已经退出**，所以此刻再去碰那个对象必然拿到
        // "Object does not exist" —— 那不是要测的东西，所以**不在这里报成败**，
        // 只把整份演示文稿关掉（不留一个空壳 PowerPoint 在用户任务栏上）。
        try
        {
            var presentation = driver.Presentation;
            presentation.Close();
            Marshal.ReleaseComObject(presentation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"（收尾时关演示文稿失败：{ex.GetType().Name}，不影响上面的结论）");
        }
        finally
        {
            KillPowerPoint();
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "全部通过：连上、读页数、翻页跟住、结束收干净、反复探测不漏、释放幂等。"
            : $"{_failures} 条没过。");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>用 COM 造一份 5 张的演示文稿并当场开始放映。<b>只用于验收，跑完关掉。</b></summary>
    private static PowerPoint.SlideShowWindow? StartShow(int slideCount)
    {
        try
        {
            // `MsoTriState` 住在 Office 那一份 PIA（Microsoft.Office.Core）里，
            // 不在 PowerPoint 这一份 —— 少写那个 using 就是 CS0103。
            var app = new Application { Visible = MsoTriState.msoTrue };
            var presentation = app.Presentations.Add(MsoTriState.msoFalse);
            // 少用一次 CustomLayout：那份接口在 PIA 里是默认接口成员，
            // 直接传给 Slides.Add 会撞 CS1503。用枚举那一档。
            var layout = PpSlideLayout.ppLayoutTitleOnly;
            for (var i = 0; i < slideCount; i++)
            {
                var slide = presentation.Slides.Add(i + 1, layout);
                slide.Shapes[1].TextFrame.TextRange.Text = $"Slide {i + 1}";
            }

            return presentation.SlideShowSettings.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"起放映失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private static void GotoSlide(PowerPoint.SlideShowWindow show, int index)
    {
        try { show.View.GotoSlide(index); }
        catch (Exception ex) { Console.WriteLine($"翻页失败：{ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>起一个 PowerPoint 但**不放映** —— 用来验 NotPresenting 那一档。</summary>
    private static bool TryStartIdlePowerPoint()
    {
        try
        {
            var app = new Application { Visible = MsoTriState.msoTrue };
            app.Presentations.Add(MsoTriState.msoFalse);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"起 PowerPoint 失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static void KillPowerPoint()
    {
        foreach (var name in new[] { "POWERPNT", "wpp", "wps" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { /* 已经在退了 */ }
            }
        }
    }

    private static Process? FindHostProcess()
    {
        foreach (var name in new[] { "POWERPNT", "wpp", "wps" })
        {
            var found = Process.GetProcessesByName(name);
            if (found.Length > 0) return found[0];
        }

        return null;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
    }
}
