namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 工具栏上一个按钮<b>是干什么的</b>。
/// <para>
/// 注意它和"哪一支笔"是两件事：<see cref="Pen"/> 只是"这是支笔"，
/// 而"这是红的那支 0.5 细笔"是那一项自己的数据。用户想要两个笔按钮，就是要有两项
/// <see cref="Pen"/>，各自带一套数据 —— 所以按钮的身份是<b>项</b>，不是这个枚举。
/// </para>
/// </summary>
internal enum ToolbarToolKind
{
    /// <summary>鼠标模式：收起画布，把桌面还给别的应用。工具栏里固定有且只有一个。</summary>
    Mouse = 0,

    /// <summary>书写。可以有多项，每项一套颜色 / 粗细 / 笔型 / 笔锋。</summary>
    Pen = 1,

    /// <summary>擦除。可以有多项，每项一套擦法 / 半径。</summary>
    Eraser = 2,

    /// <summary>撤销。固定有且只有一个。</summary>
    Undo = 3,

    /// <summary>重做。固定有且只有一个。</summary>
    Redo = 4,

    /// <summary>打开设置。固定有且只有一个 —— 它是回到"改工具栏"那个入口的唯一路径。</summary>
    Settings = 5,

    /// <summary>纯视觉分隔线。可以有多项，没有数据。</summary>
    Separator = 6,

    /// <summary>
    /// 进 / 出白板这块画布。工具栏里固定有且只有一个 —— 它是这块画布的唯一入口，
    /// 让人删掉就等于把门从里面锁上（与 <see cref="Settings"/> 同一条理由）。
    /// <para>
    /// <b>它不是"选中的工具"</b>：点它换的是"眼前是哪块画布"，不动当前选中的笔或橡皮，
    /// 所以它渲染成一颗普通按钮，而不是 <c>RadioToolToggleButton</c> ——
    /// 选中态只有一根轴（<see cref="ToolbarTools.SelectedId"/>），再挂一根就是第二个真相。
    /// </para>
    /// </summary>
    Whiteboard = 7,
}
