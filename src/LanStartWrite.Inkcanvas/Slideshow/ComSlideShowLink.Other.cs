#if !WINDOWS
using Jalium.UI;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// 非 Windows 上的<strong>内置联动占位</strong>。
/// </summary>
/// <remarks>
/// <para>
/// <b>它必须存在，而且必须说人话。</b>
/// </para>
/// <para>
/// <b>为什么需要它</b>：本应用是 <c>net10.0-windows</c> + <c>net10.0</c> 双目标，
/// 而 CI 的 deb / AppImage / 玲珑 <b>三条 Linux 腿走的正是 <c>net10.0</c> 那一条</b>
/// （见 AGENTS「安装包走 CI」那节）。COM 只在 Windows 存在，
/// 所以不按 TFM 分一份的话，Linux 构建会直接挂在"找不到 COM 那套类型"上 ——
/// <b>而那是一条只在 CI 上才红、且与改动毫无关系的路</b>。
/// </para>
/// <para>
/// <b>为什么不是"这一档在 Linux 上不可用"那么一句带过</b>：设置页会列出三档，
/// 而用户在 Linux 上选中"内置"时应该看到"这一档在这套系统上用不了"，
/// <b>而不是一个空白面板或者一个点了没反应的按钮</b>。
/// </para>
/// </remarks>
internal sealed class ComSlideShowLink : ISlideShowLink
{
    public string DisplayName => "内置联动（COM）";

    public SlideShowLinkReport Probe() => new(
        SlideShowAvailability.HostUnreachable,
        "内置联动走 COM，只在 Windows 上可用。",
        0, 0, "");

    public FrameworkElement? Thumbnail(int index) => null;

    public void Dispose()
    {
        // 没有 COM 对象可放。这里仍然写一个空实现而不是让接口继承 IDisposable 之外的东西，
        // 是为了让**三档的形状在所有 TFM 上一致** ——
        // 否则调用方就得写 `#if`，而那正是"同一件事两处写法"的开始。
    }
}
#endif
