namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 窗口层级，<b>从低到高</b>。整数就是顺序，比大小即可 —— 不需要额外的排序表。
/// <para>
/// 这套层级要回答的是"谁永远在谁上面"这一件事，而不是"谁当前被激活"。
/// 每加一个窗口都要先回答"它属于哪一层"，而不是随手写一句 <c>Topmost = true</c> ——
/// 后者在只有一个窗口时看着对，窗口一多就变成了猜拳。
/// </para>
/// </summary>
internal enum WindowLayer
{
    /// <summary>
    /// 全屏批注画布。<b>只要它在屏幕上，就必须压过其他应用</b> —— 这一条是这一层存在的理由，
    /// 由 <see cref="WindowLayerManager"/> 强制，不由调用方记得。它在本应用内部最低：
    /// 工具栏、二级菜单、对话框都在它上面。
    /// </summary>
    Canvas = 0,

    /// <summary>批注栏（工具条）。画布之上、二级菜单之下。</summary>
    Toolbar = 1,

    /// <summary>二级菜单（笔 / 橡皮）。挂在批注栏上，所以要压在批注栏之上。</summary>
    Panel = 2,

    /// <summary>设置等对话框。全应用最高，任何东西都不许盖住它 —— 包括画布。</summary>
    Dialog = 3,
}
