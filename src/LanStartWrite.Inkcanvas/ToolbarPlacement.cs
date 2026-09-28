using Jalium.UI;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 批注栏出现在哪儿：<b>工作区（不含任务栏那块）水平居中，贴在任务栏上方一点</b>。
/// <para>
/// 为什么在这儿而不是写在标记里：位置取决于"这块屏幕的工作区有多大"与"这条栏量出来多宽"，
/// 两者都是运行时的量，而 <c>Left="120"</c> 那样一个魔数只能猜 ——
/// 它猜的是"1920 宽的屏、七颗钮"，换一块屏或加两支笔就不成立了。
/// 标记里那两个数现在只是兜底（万一这句没跑到，栏还会落在看得见的位置）。
/// </para>
/// <para>
/// <b>读的是 <see cref="Jalium.UI.SystemParameters.WorkArea"/>，单位是 DIP</b>，与
/// <c>Window.Left</c> / <c>Top</c> 同一套坐标，所以这里一次换算都没有。
/// 工作区自己就把任务栏扣掉了（<c>SPI_GETWORKAREA</c>），因此"比任务栏高一点"＝"工作区下缘再抬 <see cref="BottomGap"/> DIP"，
/// 不需要去猜任务栏多高、也不需要知道它在屏幕哪一边。
/// </para>
/// <para>
/// 已知边界：<c>SPI_GETWORKAREA</c> 说的是<b>主屏</b>的工作区（WPF 同一条脾气）。
/// 把任务栏放去副屏时，栏会落在主屏下方 —— 与"跟着鼠标所在屏走"相比是取舍，不是漏。
/// </para>
/// </summary>
internal static class ToolbarPlacement
{
    /// <summary>可见表面底边到工作区下缘的距离（DIP）。</summary>
    internal const double BottomGap = 12;

    /// <summary>
    /// 透明宿主四周的留白（标记里那块 <c>Border</c> 的 Margin）。
    /// 窗口边比看得见的圆角面各多 6 DIP，"贴着任务栏上方"讲的是后者，所以算 y 时要把它减回去。
    /// </summary>
    private const double TransparentInset = 6;

    /// <summary>把批注栏摆到 <paramref name="work"/> 的下方居中处。<paramref name="work"/> 缺省取主屏工作区。</summary>
    internal static void Apply(Window bar, Rect? work = null)
    {
        var spot = Compute(work ?? Jalium.UI.SystemParameters.WorkArea, new Size(bar.Width, bar.Height));
        bar.Left = spot.X;
        bar.Top = spot.Y;
    }

    /// <summary>
    /// 放映模式那种<b>沉浸式</b>摆位：贴<b>整块屏幕</b>的下缘，而不是工作区。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="Compute"/> 的区别只有一处，但那一处就是这个模式的意义：
    /// 基准从<b>工作区</b>（扣掉任务栏）换成<b>整块屏幕</b>。放映时投影幕布铺满整块屏，
    /// 批注栏要跟着一起铺到最底边去，而不是在任务栏上方留出一道缝 ——
    /// 那一道缝在演示时非常显眼，而它对用户没有任何用处。
    /// </para>
    /// <para>
    /// <b>只换基准，不改居中</b>：栏还是水平居中。理由与"工作区居中"那条一样 ——
    /// 位置是"屏有多宽、栏有多宽"两个运行时的量，标记里的魔数只能猜。
    /// </para>
    /// <para>
    /// <b>为什么不用 <c>VirtualScreen*</c></b>：那套是<b>所有显示器</b>拼起来的桌面，
    /// 副屏在左边时它比主屏大得多，于是"居中"会居到两块屏中间那道缝上。
    /// 这里要的是"本块屏"，所以用 <c>PrimaryScreenWidth/Height</c>，与
    /// <c>WorkArea</c> 同一条已知边界（都只说主屏）。
    /// </para>
    /// </remarks>
    internal static Point ComputeImmersiveBottom(Size size)
    {
        var width = Jalium.UI.SystemParameters.PrimaryScreenWidth;
        var height = Jalium.UI.SystemParameters.PrimaryScreenHeight;
        return Compute(new Rect(0, 0, width, height), size, bottomGap: 0);
    }

    /// <summary>把批注栏按沉浸式贴到屏底。<paramref name="bar"/> 的尺寸必须已经量好。</summary>
    internal static void ApplyImmersiveBottom(Window bar)
    {
        var spot = ComputeImmersiveBottom(new Size(bar.Width, bar.Height));
        bar.Left = spot.X;
        bar.Top = spot.Y;
    }

    /// <summary>
    /// 同一个 <paramref name="work"/> 里，一块 <paramref name="size"/> 大的栏该摆的左上角。
    /// <para>纯函数：验收拿一张合成工作区就能算，不必真的把窗口搬到屏幕上。</para>
    /// </summary>
    /// <param name="bottomGap">底边到 <paramref name="work"/> 下缘的距离。沉浸式传 0（贴边）。</param>
    internal static Point Compute(Rect work, Size size, double bottomGap = BottomGap)
    {
        var x = work.X + Math.Max(0, (work.Width - size.Width) / 2);
        var y = work.Bottom - size.Height + TransparentInset - bottomGap;

        // 栏比工作区还宽还高时（超小屏、负 DPI 读数）宁可贴着上缘，也不要跑到工作区外面去。
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - size.Width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - size.Height));
        return new Point(x, y);
    }
}
