#if WINDOWS
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Jalium.UI;
using Jalium.UI.Controls;
using LanStartWrite.Inkcanvas.Diagnostics;
using Microsoft.Office.Interop.PowerPoint;

// 别名：直接 `using ...PowerPoint;` 会让 **Application 同时指 PowerPoint 的和 Jalium 的**
// （CS0104）。而这两个一个是 COM 宿主、一个是本应用的 UI 宿主 ———
// 混用它们的症状是"赋上了却调不到我要的那个方法"，而编译器只会在第一处拦。
using PowerPointApp = Microsoft.Office.Interop.PowerPoint.Application;


namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// <b>内置联动</b>：本进程自己用 COM 连上正在运行的 PowerPoint / WPS，读放映到了第几页。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是"连上已经跑着的那个"而不是"自己起一个"</b>：用户是在 PowerPoint 里按 F5 放映的，
/// 幻灯片是<b>他那个进程</b>在画的。要看到"第几页"就必须问那个进程，
/// 而拿到它的唯一办法是运行对象表（ROT）—— <c>Marshal.GetActiveObject</c> 在 .NET Core 起
/// 已被移除，所以这里自己 P/Invoke 那一支（形状与 <c>tools/SlideshowProbe</c> 踩出来的一致）。
/// </para>
/// <para>
/// <b>ProgID 两个都试</b>：Office 是 <c>PowerPoint.Application</c>，
/// WPS 演示是 <c>KWPP.Application</c>（旧版 <c>WPP.Application</c>）。
/// 而它们暴露的是同一套 PowerPoint 对象模型，所以下面那些读法两边通用 ——
/// 这就是"用 COM 就能兼容 Office 和 WPS"的确切含义。
/// </para>
/// <para>
/// <b>释放是这一类的全部难点，这里逐条做了</b>：
/// <list type="number">
/// <item><b>不释放宿主那个进程。</b>我们只是"问了几句话"，
/// 而 <c>Application.Quit()</c> 会把<b>用户正在演示的</b> PPT 关掉。
/// 所以只 <c>ReleaseComObject</c>，绝不 Quit —— 那个动作属于用户。</item>
/// <item><b>RCW 逆序释放</b>：COM 对象的 RCW 之间有引用计数依赖，
/// 顺序错了会让计数提前归零，于是后面那次释放 <c>ReleaseComObject</c> 打到别人的对象上。</item>
/// <item><b>每一层都 <c>ReleaseComObject</c></b>：只放最外层那个是最常见的漏法，
/// 而症状是"退出放映几次之后，PowerPoint 的内存 steadily 涨"——
/// 它不报错，只是慢，而且<b>与本应用看起来毫无关系</b>。</item>
/// <item><b>释放要幂等</b>：<c>Dispose</c> 可能被调两次（退出放映 + 应用关闭），
/// 而第二次对已释放的 RCW 再释放会抛，而那个异常从 <c>Dispose</c> 里出来很难定位。</item>
/// </list>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ComSlideShowLink : ISlideShowLink
{
    /// <summary>两个 ProgID：Office 与 WPS 演示。<b>顺序有意义</b>：先试 Office，
    /// 而本机同时装了 WPS 时，先试谁决定连上哪一个 —— 所以 Office 在前。</summary>
    private static readonly string[] ProgIds =
    [
        "PowerPoint.Application",
        "KWPP.Application",
        "WPP.Application",
    ];

    private readonly List<object> _rcw = [];
    private readonly object _gate = new();
    private bool _disposed;
    private string _lastProgId = "";

    public string DisplayName => "内置联动（COM）";

    /// <summary>
    /// 问一次"现在在放第几页"。
    /// </summary>
    /// <remarks>
    /// <b>整段进锁</b>：ROT 拿到的对象是<b>跨线程共享</b>的，
    /// 而 COM 公寓模型下不加锁地从另一个线程用它出的错是"偶发、不可复现、且与代码无关"。
    /// 锁的范围只包住"连上 + 读数 + 释放"这一小段，不包住等待。
    /// </remarks>
    public SlideShowLinkReport Probe()
    {
        lock (_gate)
        {
            if (_disposed) return new SlideShowLinkReport(SlideShowAvailability.Unknown, "联动已关闭。", 0, 0, "");

            try
            {
                return ProbeCore();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
            {
                // COM 那一侧的失败几乎全是 COMException，而它带回来的 HRESULT
                // 说的是宿主内部发生了什么、用户完全看不懂。所以这里只留类型名。
                ReleaseAll();
                return new SlideShowLinkReport(
                    SlideShowAvailability.HostUnreachable,
                    $"连不上 PowerPoint / WPS（{ex.GetType().Name}）。",
                    0, 0, "");
            }
        }
    }

    private SlideShowLinkReport ProbeCore()
    {
        ReleaseAll();

        var app = Attach();
        if (app is null)
        {
            return new SlideShowLinkReport(
                SlideShowAvailability.HostUnreachable,
                "没找到正在运行的 PowerPoint 或 WPS 演示。启动它们并开始放映后再进这一块。",
                0, 0, "");
        }
        _rcw.Add(app);

        // SlideShowWindows：放映中的窗口。<b>Count 是唯一判据</b> ——
        // 装了 Office 但没按 F5 时它是 0，而"没装"与"没放映"必须分开说。
        var shows = app.SlideShowWindows;
        if (shows is null)
        {
            return new SlideShowLinkReport(SlideShowAvailability.NotPresenting, "宿主在，但没有放映窗口。", 0, 0, "");
        }
        _rcw.Add(shows);

        if (shows.Count == 0)
        {
            return new SlideShowLinkReport(
                SlideShowAvailability.NotPresenting,
                "PowerPoint / WPS 在运行，但没有正在进行的放映。按 F5 开始放映后翻页即可联动。",
                0, 0, "");
        }

        // **索引器而不是 `.Item(1)`**：`SlideShowWindows.Item` 在 PIA 里是默认接口成员
        // （COM 集合那套），写成方法调用编译器不认；而 `shows[1]` 编译器会翻译成
        // 同一个调用 —— 症状是"看着完全一样的写法，一个过一个不过"。
        var show = shows[1];
        _rcw.Add(show);


        var presentation = show.Presentation;
        _rcw.Add(presentation);

        var slides = presentation.Slides;
        _rcw.Add(slides);

        var view = show.View;
        _rcw.Add(view);

        var count = slides.Count;
        var position = view.CurrentShowPosition;
        // 路径是墨迹的键，所以它跟页数一样不是可有可无的；
        // 没保存过的演示文稿它是空串，而那要照实说而不是编一个名字。
        var deckId = presentation.FullName ?? "";

        // **只在"变了"的时候写日志。**
        // 这一拍是有可能被问很多次的：窗口自己 250ms 问一次，而外头那个看门狗
        // 700ms 又问一次 —— 两者叠起来每秒七八次。
        // 每一次都写的话，一小时就把日志写掉 1.8 MB，**而日志会被轮转掉** ——
        // 于是"出问题那天的日志"被"没出问题时的重复"顶走了，
        // 症状是"我明明开着日志却找不到刚才那次"。
        var line = $"COM 连上 {_lastProgId}，第 {position}/{count} 页，deck「{deckId}」";
        if (line != _lastLogged)
        {
            _lastLogged = line;
            AppLog.Write("放映", line);
        }

        return new SlideShowLinkReport(SlideShowAvailability.Ready, $"正在放映（{_lastProgId}）。", count, position, deckId);
    }

    /// <summary>上一次写进日志的那一行。用来判"变了没有"。</summary>
    private string _lastLogged = "";

    /// <summary>
    /// 从运行对象表里拿一个宿主。<b>拿不到就返回 null，不抛</b>。
    /// </summary>
    private PowerPointApp? Attach()
    {
        foreach (var progId in ProgIds)

        {
            var app = TryGetActive(progId);
            if (app is null) continue;
            _lastProgId = progId;
            return app;
        }

        return null;
    }

    private static PowerPointApp? TryGetActive(string progId)
    {
        object? raw = null;
        try
        {
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type is null) return null;
            raw = Rot.TryGetActive(type.GUID);
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or ArgumentException)
        {
            return null;
        }

        return raw as PowerPointApp;
    }


    public FrameworkElement? Thumbnail(int index)
    {
        // 宿主不给缩略图：<c>Slide.Export</c> 要在放映进行中导出当前帧，
        // 代价与可靠性都不划算，而左下角那一格已经有"这一页上的墨迹"了。
        // 真要幻灯片画面，等真来源接进来时一起给（<c>ISlideThumbnailSource</c> 那一侧）。
        return null;
    }

    /// <summary>
    /// <b>逆序释放</b>每一层 RCW。<b>不 Quit 宿主</b>—— 理由见类注释那一条。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseAll();
        }
    }

    private void ReleaseAll()
    {
        // 逆序：RCW 之间有引用计数依赖，正序释放会让计数提前归零，
        // 于是后面那次 ReleaseComObject 打到已经被回收的对象上。
        for (var i = _rcw.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Marshal.IsComObject(_rcw[i])) Marshal.ReleaseComObject(_rcw[i]);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
            {
                // 已经失效了（宿主那边自己先关了）。**这不算失败** ——
                // 目标是"别漏"，而漏不掉的东西已经在别处被释放过了。
            }
        }

        _rcw.Clear();
    }

    /// <summary>旋转表里取一个正在运行的实例。形状与 <c>tools/SlideshowProbe</c> 踩出来的一致。</summary>
    /// <summary>
    /// 从运行对象表里拿一个正在运行的宿主。
    /// </summary>
    /// <remarks>
    /// <b>只调 <c>oleaut32!GetActiveObject</c> 这一支</b>，不去手写旋转表遍历。
    /// <para>
    /// 手写那条路（<c>GetRunningObjectTable</c> + 自己声明 <c>IRunningObjectTable</c> vtable
    /// 再按索引调槽）在这个仓库里试过三轮，全红：
    /// <list type="bullet">
    /// <item>不带 <c>[ComImport]</c> → <c>EntryPointNotFoundException</c>
    /// （把它当导出函数找，而 COM 接口方法不是导出符号）—— <b>而且编译得过</b>；</item>
    /// <item>裸指针 <c>(IRunningObjectTable)tablePtr</c> → <c>InvalidCastException</c>；</item>
    /// <item><c>out IntPtr</c> + <c>GetObjectForIUnknown</c> → <b><c>E_NOINTERFACE</c></b>。</item>
    /// </list>
    /// 而 <c>oleaut32!GetActiveObject</c> 就是被移除的 <c>Marshal.GetActiveObject</c> 的本体，
    /// 它<b>本来就在 oleaut32.dll 里导出</b>，按 CLSID 进去、IUnknown 出来，
    /// 内部那套查找由它自己做。`tools/SlideShowLinkProbe` 实测连上了跑着的 PowerPoint。
    /// </para>
    /// <para>
    /// 教训：<b>能用一条系统调用说清的事，不要自己重建中间层</b> ——
    /// 中间层里每一个自己声明的 COM 签名都是一个「编译得过、运行时炸」的地方，
    /// 而那三种红都发生在<b>界面上完全看不见</b>的地方。
    /// </para>
    /// </remarks>
    private static class Rot
    {
        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(
            ref Guid rclsid,
            IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        public static object? TryGetActive(Guid clsid)
        {
            var id = clsid;
            object? result = null;
            try
            {
                GetActiveObject(ref id, IntPtr.Zero, out result);
            }
            catch (COMException)
            {
                // 旋转表里没有这一项 —— **这是正常状态，不是错误**：
                // 用户没开 PowerPoint 时就该走到这里，而抛出去的话症状是
                // 「进这一块就崩」，而真实原因只是「他没开」。
                return null;
            }

            return result;
        }
    }
}
#endif
