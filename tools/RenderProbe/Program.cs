using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Jalium.UI.Interop;

namespace LanStartWrite.Inkcanvas.Tools;

/// <summary>
/// 量"这台机器上每个渲染后端到底能不能用"。带 <c>--one &lt;后端&gt;</c> 时只量那一个，
/// 不带参数时**每种后端各起一个子进程**去量。
/// </summary>
/// <remarks>
/// 判据是三件都要看，缺一件都会得出"能用"这种假结论：
/// 建上下文没抛、<c>Backend</c> 真的是自己要的那一档（框架在失败时会静默落回别的档）、
/// 设备状态检查通过。**只测第一件是不够的** —— 一个落回 D3D12 的 Vulkan 请求
/// 会"建成了"，而它恰好是本探针要抓的那种假绿。
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--one")
        {
            return MeasureOne(args[1]);
        }

        return MeasureAll();
    }

    /// <summary>每种后端一个独立进程，理由见文件顶部那段注释。</summary>
    private static int MeasureAll()
    {
        Console.WriteLine($"机器：{System.Environment.OSVersion}  架构：{RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine("原生后端 DLL 是否在发布目录里：");
        foreach (var name in new[] { "d3d12", "vulkan", "software" })
        {
            var found = Directory.Exists(AppContext.BaseDirectory)
                && Directory.GetFiles(AppContext.BaseDirectory, $"jalium.native.{name}.dll").Length > 0;
            Console.WriteLine($"  jalium.native.{name}.dll  {(found ? "在" : "不在")}");
        }
        Console.WriteLine();

        var exe = Environment.ProcessPath!;
        var unusable = 0;
        foreach (var backend in new[] { "Auto", "D3D12", "Vulkan", "Metal", "Software" })
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--one");
            start.ArgumentList.Add(backend);

            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEnd().Trim();
            child.WaitForExit();
            Console.WriteLine(output);
            if (output.Contains("不能用", StringComparison.Ordinal)) unusable++;
        }

        Console.WriteLine($"五种里 {unusable} 种不能��。");
        return 0;
    }

    private static int MeasureOne(string name)
    {
        if (!Enum.TryParse<RenderBackend>(name, out var wanted))
        {
            Console.WriteLine($"{name}：不是合法的后端名");
            return 2;
        }

        Console.Write($"{wanted,-9} ");
        try
        {
            var context = RenderContext.GetOrCreateCurrent(wanted, GpuPreference.Auto, forceReplace: true);
            var landed = context.Backend;
            var same = landed == wanted || wanted == RenderBackend.Auto;

            // 框架在失败时会静默落回别的后端，所以"建成了"不等于"要的那一档"。
            if (!same)
            {
                Console.WriteLine($"不能用：要的是 {wanted}，实际落回 {landed}");
                return 1;
            }

            var adapter = context.GetAdapterInfo();
            var adapterText = adapter?.Name is { Length: > 0 } adapterName
                ? $"{adapterName}（{adapter?.AdapterType}）"
                : "读不到";
            var device = context.CheckDeviceStatus() ? "正常" : "不正常";

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"可用：落点={landed} 引擎={context.DefaultRenderingEngine} 设备={device} 适配器={adapterText} 句柄=0x{context.Handle:X} 代次={context.Generation}"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"不能用：{ex.GetType().Name} {ex.Message}");
            return 1;
        }
    }
}
