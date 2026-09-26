using System.Diagnostics;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 「把本应用设成默认图片查看器」这件事的平台那一半。
/// <para>
/// <b>它不是一个能靠写注册表 / 写配置就静默办成的事</b>，两个平台各有各的规矩，
/// 而这一条限制决定了整个交互该怎么设计，所以单独写出来并在设置页里如实说明：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Windows 10 起</b>：默认打开方式由 <c>UserChoice</c> 管，带哈希与保护，
/// 应用<b>只能注册</b>自己，然后由用户在「设置 › 应用 › 默认应用」里点头。
/// 任何声称"一键设为默认"的第三方代码要么走提权改注册表（可能失败、可能被系统还原），
/// 要么干脆是假的。所以这里只<b>把系统那一页拉起来</b>，把该由用户做的那一下交回用户。
/// </item>
/// <item>
/// <b>Linux</b>：走 XDG 的 <c>xdg-mime default</c>，写的是用户自己的 mimeapps.list，
/// 应用可以自己完成。所以这条路上是真的能一键办成的。
/// </item>
/// </list>
/// </summary>
internal static class DefaultImageViewer
{
    /// <summary>Linux 侧的 desktop 文件名，与 <c>packaging/linux</c> 里那一份同名（见 AGENTS 的 appid 一节）。</summary>
    private const string LinuxDesktopId = "io.github.wwiinnddyy.lanstartwrite.desktop";

    /// <summary>要接管的类型。挑的是 Jalium 真能解的那几种（见 <c>BitmapDecoder</c> 的具体解码器）。</summary>
    private static readonly string[] MimeTypes =
    [
        "image/png", "image/jpeg", "image/bmp", "image/gif", "image/tiff",
    ];

    /// <summary>设置页那一行下面那句话。两个平台说不同的话，因为它们要用户做的事不同。</summary>
    internal static string HintText => OperatingSystem.IsWindows()
        ? "Windows 不允许应用静默改默认打开方式，需要你在系统里点一次确认。下面这只会把系统那一页打开；注册由安装包完成。"
        : "会把这几种图片格式的默认打开程序写成本应用（只影响你自己的账户）。";

    /// <summary>把用户带到"能改默认打开方式"的那一屏。Windows 拉系统设置页，Linux 直接改。</summary>
    internal static void OpenSettingsFor(Window owner)
    {
        if (OperatingSystem.IsWindows())
        {
            // ms-settings: 是 Windows 10 起的协议。设不了"哪一屏"就退到"默认应用"总页 ——
            // 拉一个不存在的子页只会得到一个什么都没发生的窗口。
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
            // 桌面上没有 xdg-mime（很简的容器）不是错误：用户仍然可以手动在文件管理器里选。
            Trace.WriteLine(ex);
        }
    }
}
