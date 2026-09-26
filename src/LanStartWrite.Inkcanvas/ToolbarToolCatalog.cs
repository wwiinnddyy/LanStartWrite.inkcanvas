using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 一条工具的<b>元数据</b> —— 对应 Class Island 的 <c>ComponentInfo</c>。
/// <para>
/// 抄的是那个项目里最要紧的一条设计：<b>"一个组件是什么"与"用户放了几个"是两张表</b>。
/// Class Island 那边是 <c>ComponentInfo</c>（注册表里的元数据）与 <c>ComponentSettings</c>
/// （放置的实例，靠 <c>AssociatedComponentInfo</c> 指回元数据）。这边对应
/// <see cref="ToolbarToolKind"/> + 本表，与 <see cref="ToolbarTool"/>。
/// </para>
/// <para>
/// 为什么必须是两张：<c>Kind</c> 上写着"笔"，可"红色 0.5 细的那支"是<b>用户数据</b>。
/// 不拆开就会出现"菜单里改颜色，所有笔一起变"—— 而那正是这个项目早先踩过的坑。
/// 组件库列的是本表，工具栏上摆的是实例，两边靠 <see cref="Kind"/> 关联。
/// </para>
/// </summary>
/// <param name="Kind">这一条是什么。</param>
/// <param name="Name">显示名（目录与提示里念出来的那份）。</param>
/// <param name="Glyph">图标码点。取自库实测表，不许凭记忆写。</param>
/// <param name="Description">这个人话解释：它是干什么的、为什么能（或不能）再加一个。</param>
/// <param name="Repeatable">能不能放多项。</param>
internal sealed record ToolbarToolCatalogEntry(
    ToolbarToolKind Kind,
    string Name,
    ushort Glyph,
    string Description,
    bool Repeatable);

/// <summary>
/// 工具的元数据表。<b>一份，只有这一份。</b>
/// <para>
/// 类比 Class Island 的 <c>ComponentRegistryService</c>：组件自己登记元数据，
/// 界面照登记渲染。这里没有插件系统，所以是一张静态表 + 一个按 kind 查的入口 ——
/// 但"目录那一页由元数据生成"这条性质与它一样，也是"界面里没有一行是手写的类别"。
/// </para>
/// </summary>
internal static class ToolbarToolCatalog
{
    /// <summary>全部条目，按工具栏上的默认次序（可重复的在前，固定的按"进出画布的顺序"在后）。</summary>
    internal static IReadOnlyList<ToolbarToolCatalogEntry> Entries { get; } =
    [
        new(ToolbarToolKind.Pen, "书写笔", 0xE76D,
            "落笔写字、画画。每一支各带一套颜色、粗细、笔型与笔锋，互不影响。", true),
        new(ToolbarToolKind.Eraser, "橡皮", 0xE75C,
            "擦掉墨迹。可以多把，各带一套擦法（面积擦 / 笔迹擦）与半径。", true),
        new(ToolbarToolKind.Separator, "分隔线", 0xE76E,
            "纯视觉的一条竖线，把长工具栏切成几段。", true),

        new(ToolbarToolKind.Mouse, "鼠标模式", 0xE7C9,
            "收起批注、把桌面还给别的应用。退出这块画布的唯一入口，固定有一枚，不可再加也不可删。", false),
        new(ToolbarToolKind.Whiteboard, "白板", 0xE786,
            "进 / 出白板这块画布。那块画布的唯一入口，固定有一枚，不可再加也不可删。", false),
        new(ToolbarToolKind.Image, "图片", 0xE8B9,
            "打开一张图并在它上面批注。看图批注的唯一入口，固定有一枚，不可再加也不可删。", false),
        new(ToolbarToolKind.Pdf, "PDF", 0xE8C5,
            "打开一份 PDF 并在上面批注。一份 PDF 开一个窗口，页与页之间连续滚动；固定项，只有一枚", false),
        new(ToolbarToolKind.Undo, "撤销", 0xE7A7,
            "退回上一步。基本盘，固定有一枚，不可再加也不可删。", false),
        new(ToolbarToolKind.Redo, "重做", 0xE7A6,
            "重做被撤销的那一步。基本盘，固定有一枚，不可再加也不可删。", false),
        new(ToolbarToolKind.Settings, "设置", 0xE713,
            "打开设置。回到这一页的唯一入口，固定有一枚，不可再加也不可删。", false),
    ];

    /// <summary>按种类查一份。认不出来的种类退回"未知"那条，而不是抛 ——
    /// 存档里多出一个新种类时，设置页要能显示它，而不是整页打不开。</summary>
    internal static ToolbarToolCatalogEntry For(ToolbarToolKind kind) =>
        Entries.FirstOrDefault(entry => entry.Kind == kind) ?? new ToolbarToolCatalogEntry(
            kind, kind.ToString(), 0xE7C9, "未知种类。", false);

    /// <summary>图标（走 <see cref="ToolbarToolVisuals"/> 那一份码点表，catalog 只带码点）。</summary>
    internal static FontIcon Icon(ToolbarToolCatalogEntry entry, double size) => new()
    {
        Glyph = ((char)entry.Glyph).ToString(),
        FontFamily = "Segoe Fluent Icons",
        FontSize = size,
    };

    /// <summary>用户数据里有没有这一类的实例（目录上标"已在工具栏上"用）。</summary>
    internal static bool IsOnToolbar(ToolbarToolKind kind) =>
        ToolbarTools.Items.Any(tool => tool.Kind == kind);
}
