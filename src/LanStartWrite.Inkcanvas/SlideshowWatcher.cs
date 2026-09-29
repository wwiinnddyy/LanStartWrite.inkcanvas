using System.Diagnostics;
using LanStartWrite.Inkcanvas.Diagnostics;
using LanStartWrite.Inkcanvas.Slideshow;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// <b>盯着宿主那边有没有在放映</b>，并把"开始了 / 结束了"这两件事转给批注栏。
/// </summary>
/// <remarks>
/// <para>
/// <b>它为什么必须存在</b>：联动那一侧是<b>拉取</b>的 —— 想知道"放到第几页"必须有人去问。
/// 而在它之前，**没有人问**，除非用户手动点进放映场景。
/// 于是用户在 PowerPoint 里按了 F5 之后本应用什么也不做：
/// 不切场景、墨迹不跟、左下角那个页面控件也不出现。
/// <b>那三样是同一个原因</b>，而这个类是那一个原因的唯一解。
/// </para>
/// <para>
/// <b>先用进程名单做一道便宜的闸，再碰 COM</b>。这一条不是优化，是必需的：
/// 每一次 <see cref="ISlideShowLink.Probe"/> 都要过一遍旋转表、拿 RCW、再逐层释放；
/// 而 90% 的时间用户压根没开 PowerPoint。不先看进程的话，
/// 我们会为了"确认没人放映"而每秒问 COM 十几次 —— 那不只费电，
/// 还在<b>用户没装 Office 的机器上</b>每次都去问一遍 COM。
/// </para>
/// <para>
/// <b>只在两个沿上动</b>：刚进入放映时进、刚结束时退。中间每一拍都只是"还是那样"。
/// 不这样做的话，用户中途手动切去白板会被下一拍拽回来 —— 而他主动切走的意思
/// 恰恰是"我现在不想批注这一页"。
/// </para>
/// </remarks>
internal sealed class SlideShowWatcher : IDisposable
{
    private readonly AnnotationToolbarWindow _toolbar;
    private readonly DispatcherTimer _timer;
    private bool _wasPresenting;
    private bool _disposed;
    private int _ticks;

    /// <summary>宿主在 Windows 上的进程名。**WPS 的两个名字一并看着** ——
    /// 只认 PowerPoint 的话，WPS 用户会得到"什么都没发生"而且连日志都没有。</summary>
    private static readonly string[] HostProcessNames = ["POWERPNT", "wpp", "wps"];

    internal SlideShowWatcher(AnnotationToolbarWindow toolbar)
    {
        _toolbar = toolbar;
        _timer = new DispatcherTimer
        {
            // 700ms：这个值定的是"用户按下 F5 之后多久本应用跟上来"。
            // 更短只是白烧 CPU（而 COM 那条路每趟都要建/放 RCW），
            // 更长则用户已经察觉到"没反应"了。半秒上下是这类联动的常规量级。
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _timer.Tick += (_, _) => Tick();
    }

    internal void Start()
    {
        _timer.Start();
        // 开跑之前先问一次：用户可能在我们起来之前就已经在放映了，
        // 而那正是"启动应用就看到批注层盖在放映上"的场景 ——
        // 不问的话，**要等下一个沿**才进，而那个沿可能要等到下次放映。
        Tick();
    }

    private void Tick()
    {
        if (_disposed) return;

        // **这里绝不能因为"放映窗口已经在了"就跳过探测。**
        // 跳过的话，看门狗就**永远看不到下降沿**：放映结束之后 `_wasPresenting` 一直是 true，
        // 于是用户**第二次**按 F5 时 `presenting == _wasPresenting`，一次都不触发。
        // 症状是"第一次能进、之后再也进不去"，而日志上只有一行孤零零的
        // 「COM 连上…」没有「宿主开始放映」—— 那两行不成对，就是这条。
        //
        // 而省下来的那点 COM 调用**不值这个风险**：真正该省的是"没在放映时别问 COM"，
        // 那由下面的进程名单闸负责，它才是可靠且便宜的。
        if (!AnyHostRunning())
        {
            SetPresenting(false);
            return;
        }

        ISlideShowLink link;
        try
        {
            // **每次现造现放**：一个常驻的 link 会一直握着上一份 COM 引用，
            // 而这里要的只是"这一拍是不是在放映"这一个布尔。
            // 造一个、问一次、扔掉，比留着一个安全 —— 而这正是"释放"那几条
            // 唯一需要被反复验的地方（见 tools/SlideShowLinkProbe）。
            link = SlideShowLinkCatalog.Create(AppPreferences.Current.SlideShowLinkMode);
        }
        catch (Exception ex) when (ex is DllNotFoundException or PlatformNotSupportedException)
        {
            // 非 Windows 上没有 COM。这**不是错误**，而"这一档在 Linux 上用不了"的
            // 正常表现；不接住的话整个应用会在这一拍崩掉。
            return;
        }

        bool presenting;
        try
        {
            presenting = link.Probe().IsLive;
        }
        catch (Exception ex)
        {
            AppLog.Write("放映", $"探测时抛了 {ex.GetType().Name}：{ex.Message}");
            presenting = false;
        }
        finally
        {
            link.Dispose();
        }

        SetPresenting(presenting);

        // **心跳。** 没有它，"看门狗在跑但没触发"与"看门狗根本没跑"在日志上完全一样 ——
        // 而我为分清这两件事白查了一轮（用户报告"没反应"，日志里只有孤零零一行
        // 「COM 连上」没有「宿主开始放映」，而我一度看不出看门狗是死是活）。
        // 12 秒一行，量小到不会把日志冲掉，却足以区分这两种情况。
        if (++_ticks % 12 == 0)
        {
            AppLog.Write("放映", $"看门狗心跳：在放映={presenting}（上次沿={_wasPresenting}）");
        }
    }

    /// <summary>把"变了没有"这件事转给批注栏。<b>只有两个沿会往下发</b>。</summary>
    private void SetPresenting(bool presenting)
    {
        if (presenting == _wasPresenting) return;
        _wasPresenting = presenting;
        AppLog.Write("放映", presenting ? "宿主开始放映 → 进放映批注" : "放映结束 → 收起放映批注");

        if (presenting) _toolbar.EnterSlideShowOnShowStarted();
        else _toolbar.LeaveSlideShowOnShowEnded();
    }

    private static bool AnyHostRunning()
    {
        foreach (var name in HostProcessNames)
        {
            // 拿到就立刻放掉进程对象：它也是一个能被忘在手里的系统资源。
            var processes = Process.GetProcessesByName(name);
            if (processes.Length == 0) continue;
            foreach (var process in processes) process.Dispose();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
