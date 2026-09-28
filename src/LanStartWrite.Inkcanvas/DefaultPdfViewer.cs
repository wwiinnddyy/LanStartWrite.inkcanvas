using System.Diagnostics;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 把本应用登记成**默认 PDF 阅读器**，交给平台那一页。
/// </summary>
/// <remarks>
/// <para>
/// <b>照 <see cref="DefaultImageViewer"/> 抄的那套理由</b>，一条不多一条不少：
/// Windows 10 起"默认打开方式"的 <c>UserChoice</c> 带哈希保护，进程能写的只有
/// <c>ProgId</c> 与 <c>OpenWithProgids</c>（安装器已经写好了），而"把某个应用设为默认"
/// 这一步**必须由系统设置那一页**走。
/// </para>
/// <para>
/// <b>为什么 PDF 这一侧本来是缺的</b>：安装器为 <c>.pdf</c> 注册了 ProgId，
/// 也就是说双击 PDF 时本应用本来就在候选列表里，但设置页没有任何入口告诉用户这件事 ——
/// 一个注册了却找不到的选项，用户只会当成"没装上"。
/// </para>
/// </remarks>
internal static class DefaultPdfViewer
{
    /// <summary>Linux 的 desktop 文件名与 <c>packaging/linux</c> 下那一份同一个 appid，见 AGENTS 那节「appid 一处定义」。</summary>
    private const string LinuxDesktopId = "io.github.wwiinnddyy.lanstartwrite.desktop";

    private static readonly string[] MimeTypes = ["application/pdf"];

    /// <summary>这一行是用户唯一能确认"它去的是哪一页、点什么"的地方。</summary>
    internal static string HintText => OperatingSystem.IsWindows()
        ? "Windows 把默认应用的选择放在系统的「默认应用」那一页，程序自己改不了。安装时已经把这个应用登记进 PDF 的候选列表，去系统那一页挑一下即可。"
        : "把 PDF 的默认打开方式设为本应用（等价于命令行 xdg-mime default）";

    /// <summary>Windows 去系统那一页；Linux 直接设。</summary>
    internal static void OpenSettingsFor(Window owner)
    {
        if (OperatingSystem.IsWindows())
        {
            // ms-settings: 是 Windows 10 起的"缺省应用"那页，即系统设置 → 应用 → 默认应用。
            // 一条命令打不开具体应用那一页，也不用在那一页里找是什么 —— 没有那个入口。
            StartShell("ms-settings:defaultapps");
            return;
        }

        foreach (var mime in MimeTypes)
        {
            RunTool("xdg-mime", $"default {LinuxDesktopId} {mime}");
        }

        StartShell("xdg-open");
    }

    private static void StartShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            Trace.WriteLine(ex);
        }
    }

    private static void RunTool(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            // 这台机器上没有 xdg-mime，或者发行版不给装 —— 那是别人的系统设置。
            // Trace 一下就算了，不该把设置窗口弹一个错给用户，也不该在这里替用户选。
            Trace.WriteLine(ex);
        }
    }
}
