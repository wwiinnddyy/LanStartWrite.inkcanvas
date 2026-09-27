using System.Reflection;

namespace LanStartWrite.Inkcanvas.Tools;
/// <summary>
/// 阶段 0 探针的入口。<b>每个模式回答一个具体问题</b>，而不是"跑一遍看看"。
/// </summary>
/// <remarks>
/// 判据一律是<b>量出来的数字</b>或<b>肉眼能认的图</b>，不用"没报错"当通过 ——
/// 这一点是从 PDF 那次学来的：<c>FPDF_RenderPageBitmap</c> 传了空指针进去
/// 于是"渲染成功"而其实一格没画，退出码 0、一切看着正常。
/// 所以这里每条都带一个"它证明这次调用真的做了事"的旁证。
/// </remarks>
internal static class CameraProbe
{
    internal static int Run(string[] args)
    {
        // --api：把采集相关的真实签名反射出来。写代码之前先看事实，别照着文档猜。
        if (args.Contains("--api", StringComparer.OrdinalIgnoreCase)) return RunApiDump();

        // --capability：本机能不能采集。**这一项在任何其它项之前跑**，
        // 因为"设备枚举得到 0 个"和"枚举得到 N 个但一帧都开不出来"是两件事，
        // 而后面那件在这台机器上才是真的（见下面的说明）。
        if (args.Contains("--capability", StringComparer.OrdinalIgnoreCase)) return RunCapability();

        // --devices：设备枚举 + 平台能力。不带参数跑这一项。
        if (args.Contains("--devices", StringComparer.OrdinalIgnoreCase)) return RunDevices();

        // --frames [设备索引] [宽 高 fps]：用原生 source 直接读帧，量单帧耗时。
        if (args.Contains("--frames", StringComparer.OrdinalIgnoreCase)) return RunFrames(args);

        // --ui：起真窗口量逐帧贴图。**必须起窗口** —— 裸 source 那条路在 Windows 上
        // 任何设备/格式/尺寸都 UnsupportedFormat（见 --frames），而 CameraView 会做
        // D3D 初始化，所以只有它能回答这条路通不通。
        if (args.Contains("--ui", StringComparer.OrdinalIgnoreCase)) return CameraUiProbe.Run();

        Console.WriteLine("用法：CameraProbe <模式>");
        Console.WriteLine("  --api            反射出采集栈的真实签名（构造器/属性/方法/事件）");
        Console.WriteLine("  --devices        枚举设备与它们支持的分辨率");
        Console.WriteLine("  --frames         用原生 source 直接读帧，量单帧耗时与帧率");
        Console.WriteLine("  --leak           连读 N 帧，盯托管与原生内存（每帧一换 vs 每帧释放）");
        Console.WriteLine("  --ui             起真窗口量 Image.Source 逐帧换的真实帧率（需显示）");
        return 1;
    }

    /// <summary>反射出采集栈的真实签名。</summary>
    /// <remarks>
    /// 为什么用反射而不是照着 XML 文档写：<b>文档里"这个 API 在没有 DI 时会不会
    /// 自己兜底"没写</b>，而那正是本项目最需要知道的一件事（<c>Program.cs</c> 用
    /// <c>new Application()</c>，那条 <c>UseNativeMediaPipeline</c> 注册从未发生）。
    /// 与其读文档猜，不如把类型真实摆出来。
    /// </remarks>
    private static int RunApiDump()
    {
        string[] wanted =
        [
            "CameraView", "CameraDeviceInfo", "CameraFormat",
            "INativeCameraSource", "INativeCameraSourceFactory",
            "NativeCameraSourceFactory", "NativeCameraSource",
            "MediaFrame", "IMediaFramePool", "DefaultMediaFramePool",
            "CameraFacing", "NativePixelFormat",
            "LinuxMediaCapability", "NativeMedia",
            "VideoFrameBuffer", "MicrophoneDeviceInfo",
        ];

        var asm = typeof(CameraProbe).Assembly;
        var media = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Jalium.UI.Managed")
            ?? asm;

        Console.WriteLine($"== 采集栈 API（程序集 {media.GetName().Name}）==");
        var found = 0;
        foreach (var t in media.GetExportedTypes()
            .Where(x => wanted.Contains(x.Name))
            .OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            found++;
            Console.WriteLine();
            Console.WriteLine($"=== {t.FullName}{(t.IsEnum ? "  [enum]" : t.IsInterface ? "  [interface]" : "")}");

            if (t.IsEnum)
            {
                Console.WriteLine($"    值：{string.Join(" | ", Enum.GetNames(t))}");
                continue;
            }

            foreach (var c in t.GetConstructors())
            {
                Console.WriteLine($"    ctor({Args(c.GetParameters())})");
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var p in t.GetProperties(flags))
            {
                var accessors = (p.GetGetMethod() is not null, p.GetSetMethod() is not null);
                Console.WriteLine($"    prop {Short(p.PropertyType)} {p.Name}{(p.CanWrite ? " {get;set;}" : " {get;}")}" +
                                  (accessors == (true, true) ? "" : ""));
            }

            foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName))
            {
                Console.WriteLine($"    meth {Short(m.ReturnType)} {m.Name}({Args(m.GetParameters())})");
            }

            foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"    evt  {Short(e.EventHandlerType!)} {e.Name}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(found == wanted.Length
            ? $"== 全部 {wanted.Length} 个类型都在 =="
            : $"== 只找到 {found}/{wanted.Length} 个 ==");

        // 定向补两个问题，泛搜太宽会淹掉重点：
        //  1) `MediaFrame` 是 struct 还是 class —— 决定 `TryReadFrame(out ...)` 拿到的是什么、
        //     以及 `Dispose()` 是把内存还池还是解引用。
        //  2) 平台能力探测（Linux 那条 CameraCapture）到底挂在哪个类型上。
        Console.WriteLine();
        Console.WriteLine("== 定向补查 ==");
        var frame = media.GetExportedTypes().FirstOrDefault(t => t.Name == "MediaFrame");
        if (frame is not null)
        {
            Console.WriteLine($"MediaFrame 是 {(frame.IsValueType ? "struct" : "class")}" +
                              $"，实现 {string.Join(", ", frame.GetInterfaces().Select(i => i.Name))}");
        }

        foreach (var t in media.GetExportedTypes()
            .Where(t => t.Name.Contains("Capab", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Media", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            Console.WriteLine($"  · {t.FullName}{(t.IsEnum ? " [enum]" : "")}");
            foreach (var m in t
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName))
            {
                Console.WriteLine($"      static {Short(m.ReturnType)} {m.Name}({Args(m.GetParameters())})");
            }
        }

        return 0;
    }

    private static string Args(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p => $"{Short(p.ParameterType)} {p.Name}"));

    /// <summary>本机到底能不能采集，以及"不能"卡在哪一环。</summary>
    /// <remarks>
    /// <para>
    /// **这一项是整份探针里最有价值的一条**，因为它区分了两种长得完全一样的情况：
    /// </para>
    /// <list type="bullet">
    /// <item><b>枚举得到 0 个设备</b> → 真的没插摄像头 / 驱动没装。</item>
    /// <item><b>枚举得到 N 个但一帧都开不出来</b> → 采集链缺一环。
    /// 本机实测（2026-09-27，Windows 11 25H2 26200.9457）就是这一种：
    /// <c>USB Camera</c> 与一台虚拟摄像头都枚举得到，<c>IsCaptureSupported=True</c>，
    /// 但 <c>Open</c> 一律 <c>UnsupportedFormat</c>。</item>
    /// </list>
    /// <para>
    /// 根因在框架自己的原生代码里，<c>jalium.native.media.windows/src/win_mf_camera_source.cpp:272</c>：
    /// 它向 source reader 要 <c>MFVideoFormat_RGB32</c>，而 UVC 摄像头原生产
    /// <b>NV12 / MJPG</b>，这中间必须过一道<b>色彩转换器 MFT</b>。
    /// 本机 <c>MF_CATEGORY_VIDEO_PROCESSOR</c> 整个类别未注册、
    /// 转换器 CLSID <c>{6A2745A6-86E6-11D2-9A0D-00A0C90349F0}</c> 也不存在 ——
    /// <c>MFPlat.DLL</c> 与 <c>mf.dll</c> 都在（所以枚举/激活/建 reader 都正常），
    /// 缺的恰好是"解码与色彩处理"那部分，也就是 Windows 的 Media Foundation 可选组件。
    /// </para>
    /// <para>
    /// <b>所以这一条既是探针的结论，也是实现的第一道门</b>：展台能不能开，
    /// 要在<b>开设备之前</b>就问清，而不是让用户对着一个永远不出来的画面等。
    /// </para>
    /// </remarks>
    private static int RunCapability()
    {
        var ok = true;
        Console.WriteLine("== 本机采集能力 ==");

        var supported = Jalium.UI.Controls.CameraView.IsCaptureSupported;
        Console.WriteLine($"  采集后端已加载        : {supported}");
        ok &= supported;

        IReadOnlyList<Jalium.UI.Media.Pipeline.CameraDeviceInfo> devices;
        try
        {
            devices = Jalium.UI.Controls.CameraView.EnumerateDevices();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  枚举设备抛了          : {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  设备数                : {devices.Count}");
        foreach (var d in devices)
        {
            Console.WriteLine($"    · {d.FriendlyName}（{d.SupportedFormats.Count} 种格式）");
        }

        if (devices.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("结论：**没有可用设备**。展台应当如实说「没有找到摄像头」并给出重试入口。");
            return 1;
        }

        // 有设备，那就必须真开一次 —— 因为"枚举得到"与"开得出"是两件事。
        try
        {
            using var source = new Jalium.UI.Media.Native.NativeCameraSourceFactory()
                .Create(Jalium.UI.Media.Imaging.DefaultMediaFramePool.Shared);
            source.Open(devices[0].Id, 640, 480, 30, Jalium.UI.Media.Imaging.NativePixelFormat.Bgra8);
            Console.WriteLine("  开设备                : 成功");
        }
        catch (Exception ex)
        {
            ok = false;
            Console.WriteLine($"  开设备                : **失败** —— {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("结论：**设备在、但一帧都开不出来**。这是采集链缺一环，不是「没插摄像头」。");
            Console.WriteLine("      展台必须区分这两句话：前者是「电脑缺组件，装上就好」，");
            Console.WriteLine("      后者是「没接摄像头」，而这两句给用户的指引完全不同。");
        }

        return ok ? 0 : 1;
    }

    /// <summary>设备枚举 + 平台能力。</summary>
    /// <remarks>
    /// 三件要在这里问清的事：<b>(1)</b> 有没有设备、分别是什么；
    /// <b>(2)</b> 每个设备支持哪些分辨率 —— 这是分辨率下拉框的全部内容来源，
    /// 自己编一份分辨率表就等于和设备对不上（MF 会静默降级到别的档，
    /// 症状是"我选了 720p，拿到的还是 480p"）；
    /// <b>(3)</b> 平台能力位 —— Linux 那条腿靠它决定是"真采集"还是"如实说不行"。
    /// </remarks>
    private static int RunDevices()
    {
        Console.WriteLine($"== 设备枚举（平台 {Environment.OSVersion}）==");

        // 能力位先问。枚举设备之前就该知道这台机器的采集栈在不在，
        // 否则"一个设备都没有"这件事分不清是"没插摄像头"还是"后端没加载"。
        try
        {
            var caps = Jalium.UI.Media.Native.NativeLinuxMedia.GetCapabilities();
            Console.WriteLine($"平台能力：{caps}");
            var isFlags = typeof(Jalium.UI.Media.Pipeline.LinuxMediaCapability)
                .GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0;
            Console.WriteLine($"  是 Flags 组合：{(isFlags ? "是" : "否")}  含 CameraCapture：{HasFlag(caps, Jalium.UI.Media.Pipeline.LinuxMediaCapability.CameraCapture)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"平台能力探测抛了：{ex.GetType().Name} {ex.Message}");
        }

        IReadOnlyList<Jalium.UI.Media.Pipeline.CameraDeviceInfo> devices;
        try
        {
            // 走 public 无参 ctor，不碰 DI —— 本应用根本没用 AppBuilder，
            // 那条 UseNativeMediaPipeline 注册从未发生，这正是要先验的一件事。
            var factory = new Jalium.UI.Media.Native.NativeCameraSourceFactory();
            devices = factory.EnumerateDevices();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"枚举设备抛了：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("（后端可能没加载。jalium.native.media.dll 应该在输出目录里 —— 先确认它在。）");
            return 1;
        }

        Console.WriteLine($"设备 {devices.Count} 个");
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            Console.WriteLine();
            Console.WriteLine($"[{i}] {d.FriendlyName}");
            Console.WriteLine($"    Id       : {d.Id}");
            Console.WriteLine($"    Facing   : {d.Facing}");
            Console.WriteLine($"    格式     : {d.SupportedFormats.Count} 种");
            foreach (var f in d.SupportedFormats)
            {
                Console.WriteLine($"      {f.Width}×{f.Height} @ {f.Fps:0.##} fps");
            }
        }

        if (devices.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("一个设备都没有 —— 展台在真机上会是「没有可用摄像头」，那是设计内的降级，不是 bug。");
        }

        return 0;
    }

    private static bool HasFlag<T>(T value, T flag) where T : struct, Enum => value.HasFlag(flag);

    /// <summary>用原生 source 直接读帧，量单帧耗时。</summary>
    /// <remarks>
    /// <b>每帧都验"这帧真的不一样"</b>：摄像头被占用、MF 协商失败、后端没加载，
    /// 这三种情况都可能"返回成功"而给出同一份全零缓冲 ——
    /// 那正是 PDF 那次"渲染成功却一格没画"的同构陷阱（页号 0 → NULL → 直接 return）。
    /// 所以判据是 <b>相邻两帧的差异量</b> 与 <b>非零像素占比</b>，不是"没抛异常"。
    /// </remarks>
    private static int RunFrames(string[] args)
    {
        var index = 0;
        var width = 1280;
        var height = 720;
        var fps = 30.0;
        var samples = 30;

        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        for (var i = 0; i < positional.Length; i++)
        {
            if (!int.TryParse(positional[i], out var v)) continue;
            if (i == 0) index = v;
            else if (i == 1) width = v;
            else if (i == 2) height = v;
            else if (i == 3) fps = v;
        }

        var factory = new Jalium.UI.Media.Native.NativeCameraSourceFactory();
        var devices = factory.EnumerateDevices();
        if (devices.Count == 0) { Console.WriteLine("没有设备，先跑 --devices"); return 1; }
        if (index >= devices.Count) { Console.WriteLine($"设备下标 {index} 越界（共 {devices.Count} 个）"); return 1; }

        // 像素格式：UVC 摄像头原生产出的是 NV12 / MJPG，不产 BGRA8。
        // 所以"请求 BGRA8"能不能通，取决于原生那一侧有没有插色彩转换器 ——
        // 这是 Open 失败时头一个该分清的事，而不是"这台摄像头坏了"。
        var format = Jalium.UI.Media.Imaging.NativePixelFormat.Bgra8;
        foreach (var a in args)
        {
            if (a.Equals("--rgba", StringComparison.OrdinalIgnoreCase))
                format = Jalium.UI.Media.Imaging.NativePixelFormat.Rgba8;
            if (a.Equals("--bgra", StringComparison.OrdinalIgnoreCase))
                format = Jalium.UI.Media.Imaging.NativePixelFormat.Bgra8;
        }

        // 传 0/0/0 = 让设备用它自己的默认档。很多实现只在这条路上走得通，
        // 而"请求一个恰好对不上的尺寸"会被 MF 直接判成 UnsupportedFormat。
        var useDeviceDefault = args.Contains("--native", StringComparer.OrdinalIgnoreCase);
        if (useDeviceDefault) { width = 0; height = 0; fps = 0; }

        var device = devices[index];
        Console.WriteLine($"== 读帧：{device.FriendlyName} 请求 " +
                          $"{(useDeviceDefault ? "设备默认档" : $"{width}×{height}@{fps:0.##}")} 格式={format} ==");
        Console.WriteLine($"（请求值不一定被接受，MF 会静默降级；所以下面打印的是**实际**帧尺寸）");

        using var source = factory.Create(Jalium.UI.Media.Imaging.DefaultMediaFramePool.Shared);
        try
        {
            source.Open(device.Id, width, height, fps, format);
            Console.WriteLine("Open 成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Open 抛了：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var read = 0;
        var failed = 0;
        byte[]? previous = null;
        var perFrame = new List<double>();
        var dims = new HashSet<string>();

        try
        {
            for (var i = 0; i < samples * 3 && read < samples; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!source.TryReadFrame(out var frame))
                {
                    failed++;
                    continue;
                }

                perFrame.Add(sw.Elapsed.TotalMilliseconds);
                dims.Add($"{frame.Width}×{frame.Height} stride={frame.Stride} {frame.Format}");

                // 帧内容的三项统计：非零占比、与上一帧的差异量、是否一直在变。
                var px = frame.Pixels.Span;
                long nonZero = 0;
                var checksum = 0UL;
                var step = Math.Max(1, px.Length / 65536);
                for (var p = 0; p < px.Length; p += step)
                {
                    if (px[p] != 0) nonZero++;
                    checksum += px[p];
                }

                long diff = 0;
                if (previous is not null)
                {
                    for (var p = 0; p < Math.Min(px.Length, previous.Length); p += step)
                    {
                        if (px[p] != previous[p]) diff++;
                    }
                }

                if (read == 0)
                {
                    Console.WriteLine($"  首帧 {frame.Width}×{frame.Height} 字节={px.Length} " +
                                      $"抽样非零={nonZero * 100.0 / (px.Length / step):0.0}%");
                }
                else if (read is 1 or 2 or 9 or 29)
                {
                    Console.WriteLine($"  第{read + 1}帧 与上一帧抽样差异={diff * 100.0 / (px.Length / step):0.0}%");
                }

                previous = px.ToArray();
                frame.Dispose();     // 不还池就是每秒 30 次大块缓冲堆积
                read++;
            }
        }
        finally
        {
            source.Stop();
        }

        Console.WriteLine();
        Console.WriteLine($"读到 {read} 帧，取不到 {failed} 次");
        foreach (var d in dims) Console.WriteLine($"  实际尺寸：{d}");
        if (perFrame.Count > 0)
        {
            var sorted = perFrame.OrderBy(x => x).ToArray();
            Console.WriteLine($"单帧 TryReadFrame 耗时：中位 {sorted[sorted.Length / 2]:0.00} ms  " +
                              $"最小 {sorted[0]:0.00}  最大 {sorted[^1]:0.00}");
            Console.WriteLine($"  → 纯读取的理论上限约 {1000.0 / Math.Max(0.01, sorted[sorted.Length / 2]):0.0} fps");
        }

        return 0;
    }

    private static string Short(Type t)
    {
        if (!t.IsGenericType) return t.Name switch
        {
            "Void" => "void", "Int32" => "int", "String" => "string",
            "Boolean" => "bool", "Double" => "double", "Single" => "float",
            "IntPtr" => "nint", "Object" => "object", _ => t.Name,
        };
        var stem = t.Name[..t.Name.IndexOf('`')];
        return $"{stem}<{string.Join(", ", t.GetGenericArguments().Select(Short))}>";
    }
}
