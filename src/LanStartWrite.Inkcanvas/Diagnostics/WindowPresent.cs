using System;
using System.Diagnostics;
using System.Globalization;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas.Diagnostics;

/// <summary>
/// 统一的"显示一个二级窗口"入口。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能让各处直接 <c>Show()</c></b>：用户报"二级窗口出现时先只有一个空边框，
/// 随后才有内容"。实测（<c>UiSmoke --show-probe</c>）——设置窗口在 <c>Show()</c>
/// 返回那一刻 <c>IsArrangeValid == false</c>，而白板是 <c>true</c>。
/// 也就是说那一帧呈现的是**没排过版的树**，空边框是时序而不是错觉。
/// </para>
/// <para>
/// <b>而 <c>UpdateLayout()</c> 提前到 <c>Show()</c> 之前并不管用</b>：先排好
/// （<c>IsArrangeValid == true</c>），<c>Show()</c> 一执行又被打回 <c>false</c>。
/// 所以这一帧在框架里躲不开，应用侧能做的是<b>显示完立刻同步补一次排版</b>，
/// 把首帧之后的那一帧变正确，并把这次间隔记下来。
/// </para>
/// <para>
/// <b>验收为什么从来没红过</b>：UiSmoke 到处调 <c>ForceRenderFrame()</c>，
/// 那一拍被探针强行同步掉了。<b>"测试里从不空帧"是探针给的，不是产品给的。</b>
/// 与"缩放前后图跟着重定位"只问变换对象换没换，是同一个形状的错误。
/// </para>
/// </remarks>
internal static class WindowPresent
{
    /// <summary>显示一个二级窗口，并补齐 <c>Show()</c> 之后那次没跑完的排版。</summary>
    internal static void Show(Window window, string label)
    {
        var watch = Stopwatch.StartNew();
        window.Show();

        // 排版在 Show() 之后被作废了，就地补一次；不补的话用户要多看一帧空框。
        var needsLayout = !window.IsArrangeValid || !window.IsMeasureValid;
        if (needsLayout) window.UpdateLayout();
        watch.Stop();

        var size = $"{window.ActualWidth:F0}x{window.ActualHeight:F0}";
        var verdict = needsLayout ? $"★首帧未排版（已补一次：{size}）★" : $"首帧已排版 {size}";
        AppLog.Write("窗口", string.Create(
            CultureInfo.InvariantCulture,
            $"{label} 显示{watch.Elapsed.TotalMilliseconds:F1}ms {verdict}"));
    }
}
