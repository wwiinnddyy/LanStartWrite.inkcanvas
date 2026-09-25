using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 一个工具栏项的<b>外观</b>：图标码点、色标、还有"那一格 40×40 长什么样"。
/// <para>
/// 抽出来是因为现在有两处要画同一个按钮：批注栏上那一排，和设置页「工具栏按钮」那份列表。
/// 码点抄两份的代价不是多写几行 —— 是<b>改了一处另一处不会跟着改</b>，
/// 于是"列表里是荧光笔、工具栏上却是书写笔"这种错会静默存在。
/// </para>
/// </summary>
internal static class ToolbarToolVisuals
{
    /// <summary>
    /// 图标一律走 <see cref="FontIcon"/> + 显式 <c>Segoe Fluent Icons</c>。
    /// <para>
    /// 为什么不用 <see cref="SymbolIcon"/>：这个运行时里 SymbolIcon 不暴露 FontFamily，
    /// 被框架钉死在 'Segoe MDL2 Assets'（Win10 那套形状），而且 764 格里有 120 格画不出墨。
    /// 只有 FontIcon 的显式 FontFamily 能落到 Windows 11 的字形上 —— 这条是 FluentJalium
    /// 的图标族实测（docs/astra/audits/icon-family.md §9.2）给的结论。
    /// 下面每个码点都取自它的实测表 glyph-ink-symbol.csv，且 ink 均 &gt; 0（不是那 120 格空白）。
    /// </para>
    /// </summary>
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");

    /// <summary>
    /// 图标码点。<b>一颗钮一个形状，不按笔型 / 擦法细分</b>。
    /// <para>
    /// 试过按用途细分（荧光笔用 Highlight、激光笔与笔迹擦用"实心"变体 E829 / E82C），
    /// 两个问题：实心那几个在 40×40 的小格里读起来就是一团黑（用户原话"有的按钮它是黑色的"），
    /// 而擦法之间的差别本来就该靠<b>名字与菜单</b>说清，不该靠把图标换成另一团黑。
    /// </para>
    /// <para>
    /// 码点取自 FluentJalium 的实测表且 <c>ink &gt; 0</c>；<b>这个运行时里没有激光笔与
    /// 整笔擦的专用字形</b>（<c>StrokeErase</c> / <c>PointErase</c> / <c>Marker</c> 那批 ink = 0，
    /// <c>Laser</c> 这个名字压根不存在），这也是当初误入"实心变体"那条路的起点。
    /// </para>
    /// <para>
    /// <b>同一颗钮在两块画布上可以换一个形状</b>（鼠标那颗在白板里的意思是"选择墨迹"，
    /// 于是它用 SelectAll E8B3）—— 这类"按场景换呈现、不换身份"的判断一律放在这里，
    /// 不由批注栏与设置页各判一次。理由与"码点只有一份"是同一条：
    /// 两处各写一遍，改了一处另一处不跟着改，症状是"列表里是选择、工具栏上还是鼠标"。
    /// </para>
    /// </summary>
    internal static ushort GlyphFor(ToolbarTool tool) => tool.Kind switch
    {
        // 同一颗钮在两块画布上是两件事：批注里"鼠标"= 把桌面还回去，白板里 = 挑墨迹、挪墨迹。
        // 换的是呈现，不换身份（存档里的 Id 仍是 mouse、Name 仍不动）。
        ToolbarToolKind.Mouse => CanvasSceneState.IsActive(CanvasScene.Whiteboard)
            ? (ushort)0xE8B3        // SelectAll，实测 ink=401
            : (ushort)0xE7C9,       // TouchPointer，ink=338
        ToolbarToolKind.Pen => 0xE76D,         // InkingTool
        ToolbarToolKind.Eraser => 0xE75C,      // EraseTool
        ToolbarToolKind.Undo => 0xE7A7,        // Undo
        ToolbarToolKind.Redo => 0xE7A6,        // Redo
        ToolbarToolKind.Settings => 0xE713,    // Setting
        ToolbarToolKind.Whiteboard => 0xE786,  // Slideshow，ink=389
        _ => 0xE76D,
    };

    /// <summary>
    /// 这颗钮<b>此刻</b>念作什么。<b>存档里的 <c>Name</c> 一个字都不动</b>：
    /// 改的是呈现，不是用户给这颗钮起的名字。
    /// <para>
    /// 与 <see cref="GlyphFor"/> 放在同一个类里是同一条理由 —— 批注栏与设置页那份列表画的是
    /// 同一颗钮，"按场景换说法"这件事抄两处，迟早出现"列表里写鼠标、工具栏上画选择"。
    /// </para>
    /// </summary>
    internal static string DisplayName(ToolbarTool tool) =>
        tool.Kind == ToolbarToolKind.Mouse && CanvasSceneState.IsActive(CanvasScene.Whiteboard)
            ? "选择"
            : tool.Name;

    internal static FontIcon Icon(ToolbarTool tool, double size) => new()
    {
        Glyph = char.ConvertFromUtf32(GlyphFor(tool)),
        FontFamily = FluentIconFont,
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    /// <summary>
    /// 一颗按钮的内容：居中图标 +（笔才有）底部一条色标。
    /// <para>
    /// <b>色标是"两支同色系的笔也能分辨"的唯一依据</b>，而它必须另画一条而不是给图标上色：
    /// 图标的墨水由宿主通过 IconInk 绑定，色标不会影响前景。
    /// </para>
    /// </summary>
    internal static UIElement BuildContent(ToolbarTool tool, double size)
    {
        var content = new Grid { Width = size, Height = size };
        content.Children.Add(Icon(tool, 16));

        if (tool.Kind != ToolbarToolKind.Pen) return content;

        var chip = new Border
        {
            Width = size * 0.55,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, size * 0.1),
            Background = new SolidColorBrush(Argb.Unpack(tool.ColorFor(CanvasSceneState.Active))),
            IsHitTestVisible = false,
        };
        content.Children.Add(chip);
        return content;
    }

    /// <summary>分隔线的样子。宽度高度是布局常量，颜色走主题 token。</summary>
    internal static Border Separator()
    {
        var separator = new Border
        {
            Width = 1,
            Height = 26,
            // 先给一个本地值兜底：下面那句具名 token 万一在库里不存在，分隔线至少还离得开邻居。
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        separator.SetResourceReference(FrameworkElement.MarginProperty, "AppBarSeparatorMargin");
        separator.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return separator;
    }
}
