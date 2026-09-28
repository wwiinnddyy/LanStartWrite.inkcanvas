using System;
using System.Collections.Generic;
using System.Linq;
using FluentJalium.Controls;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas.Slideshow;

/// <summary>
/// 三档联动方式的<b>元数据表</b>，外加"按当前选中的那一档建出该有的设置项"。
/// </summary>
/// <remarks>
/// <para>
/// <b>换一档，底下那张卡片要跟着换</b> —— 那是用户明确要的。
/// 而"哪里变了"如果写成 <c>switch</c> 再去改别的控件的 Visible，
/// 那么<b>加一档就要改两个地方</b>，漏一处就是"新的一档带着旧的一档的项出现"，
/// 而且界面上完全看不出错。所以每一档<b>自己说</b>它带哪些项（见
/// <see cref="SlideShowLinkDescriptor.BuildSettings"/>），这里只负责按索引取到那一档。
/// </para>
/// <para>
/// <b>眼下只有「内置」是实现了的</b>，另两档先占住位置。
/// 它们各自带的项是<b>它们自己要装的东西</b>（一个插件路径、一个端口）——
/// 因为外置插件的本质就是"另一套东西在两侧说话"，而用户需要知道怎么指到它。
/// </para>
/// </remarks>
internal static class SlideShowLinkCatalog
{
    /// <summary>全部档位，按设置页下拉里的次序。</summary>
    internal static IReadOnlyList<SlideShowLinkDescriptor> Entries { get; } =
    [
        new(SlideShowLinkMode.BuiltIn, "内置联动",
            "本应用自己用 COM 连上正在运行的 PowerPoint 或 WPS 演示，读它放到了第几页。什么都不用装。",
            Available: true),

        new(SlideShowLinkMode.OfficeLink, "OfficeLink",
            "外置插件：由 OfficeLink 那一套与 PowerPoint 说话，本应用只收结果。需要先装好它。",
            Available: false),

        new(SlideShowLinkMode.PptMo, "pptmo",
            "外置插件：由 pptmo 那一套与 PowerPoint 说话，本应用只收结果。需要先装好它。",
            Available: false),
    ];

    /// <summary>按标识取一档；认不出来退回<strong>内置</strong>而不是抛 ——
    /// 存档里多出一个新种类时，设置页要能显示它，而不是整页打不开。</summary>
    internal static SlideShowLinkDescriptor For(SlideShowLinkMode mode) =>
        Entries.FirstOrDefault(entry => entry.Mode == mode) ?? Entries[0];

    /// <summary>下拉里每一项的显示名（与 <see cref="Entries"/> 同序）。</summary>
    internal static IReadOnlyList<string> ChoiceLabels { get; } =
        [.. Entries.Select(entry => entry.Name)];

    /// <summary>造出当前那一档的实现。<b>调用方负责 Dispose</b>。</summary>
    internal static ISlideShowLink Create(SlideShowLinkMode mode) => mode switch
    {
        // 外置那两档眼下没有实现，而"造一个什么都不做的对象"比"造不出来"更糟：
        // 前者让设置页与窗口照常跑，只是那份报告说 HostUnreachable，
        // 而用户看到的是一句人话；后者会在选中那一档时直接抛，
        // 症状是"下拉里选了 pptmo，整个设置页卡住"。
        _ => new ComSlideShowLink(),
    };

    /// <summary>
    /// 把「放映管理」那一节的**设置项**建出来 —— 除了第一项"联动方式"之外的那些。
    /// </summary>
    /// <remarks>
    /// <b>刻意不由各档自己建控件</b>：控件归设置页，档位只提供"项"的数据。
    /// 否则每个档位都要认识设置页的样式资源，而那种反向依赖会在换主题时炸开。
    /// </remarks>
    internal static void BuildModeSettings(SlideShowLinkMode mode, StackPanel target, Action onModeChanged)
    {
        target.Children.Clear();
        var descriptor = For(mode);
        var host = new SlideShowSettingsHost(target, onModeChanged);

        if (descriptor.Mode == SlideShowLinkMode.BuiltIn)
        {
            host.Add(Row("接入方式",
                "连正在运行的那一个宿主进程，而不是自己再起一个 —— 幻灯片是用户那个 PowerPoint 在画的。" +
                "本机装了 WPS 的话也一样连上：它暴露的是同一套对象模型。" +
                "读数是轮询的，所以翻页到笔迹跟上有一拍左右的延迟；换来的是不会「翻页之后永远停在上一页」。"));
            host.Add(Row("放映状态", "这一格会显示连上了没有、放到第几页、是哪一份文件。没在放映时它写明该怎么开始放映，而不是一句「不可用」。"));
        }
        else if (descriptor.Mode == SlideShowLinkMode.OfficeLink)
        {
            host.Add(Row("需要先装 OfficeLink",
                "这一档还没接上：外置插件那一侧还没有实现。选它之后这一块会显示一句人话，而不是什么也不发生 —— " +
                "两者你都能看出自己没接上，区别只在于看不看得见原因。"));
        }
        else
        {
            host.Add(Row("需要先装 pptmo",
                "这一档还没接上：外置插件那一侧还没有实现。选它之后这一块会显示一句人话，而不是什么也不发生。"));
        }

        foreach (var element in descriptor.BuildSettings(host, mode)) host.Add(element);
    }

    /// <summary>
    /// 一行设置：左边标题 + 说明，右边一个动作控件（没有就给 <c>null</c>）。
    /// </summary>
    /// <remarks>
    /// <b>样式键一律用 <c>SetResourceReference</c> 走应用自己那一份</b>，
    /// 不在这里写死颜色或间距。原因与工具栏那族控件一致：主题变体是
    /// <c>FluentThemeManager</c> 在换的，一处写死就等于第二套尺度，
    /// 而第二套尺度只在深色主题下才显形（那时看起来像"配色没跟上"）。
    /// </remarks>
    internal static FrameworkElement Row(string title, string body, FrameworkElement? action = null)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = title };
        heading.SetResourceReference(TextBlock.StyleProperty, "BodyTextBlockStyle");
        var help = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
        help.SetResourceReference(TextBlock.StyleProperty, "HelperTextBlockStyle");
        help.SetResourceReference(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        left.Children.Add(heading);
        left.Children.Add(help);

        var row = new FluentSettingsRow();
        row.SetResourceReference(FrameworkElement.MinWidthProperty, 0.0);
        row.Children.Add(left);
        if (action is not null)
        {
            action.SetResourceReference(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.Children.Add(action);
        }

        var card = new Border { Child = row };
        card.SetResourceReference(Control.StyleProperty, "SettingsCardStyle");
        return card;
    }
}
