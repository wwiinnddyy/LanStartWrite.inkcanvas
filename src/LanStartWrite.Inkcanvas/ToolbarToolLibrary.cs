using FluentJalium.Controls;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 设置页「工具菜单」那一页：<b>组件库</b> —— 照
/// <see cref="ToolbarToolCatalog"/> 那张元数据表生成的一格格工具。
/// <para>
/// 抄的是 Class Island 的组件库面板：那边是 <c>WrapPanel</c>（<c>components-panel</c>
/// 那个样式就是给它准备的），一格一个组件，每格<b>拖出去</b>放进容器；那边容器里的组件
/// 也拖得出、拖得回。这里同构：一格一格排开、满了自动换行，拖到「工具栏」那一排上就加进去。
/// </para>
/// <para>
/// <b>加进去靠拖，不靠加号按钮。</b> Class Island 那套交互里容器只接两种拖：
/// 从库里拖 <c>ComponentInfo</c> 是"新建一个"，在列表里拖 <c>ComponentSettings</c> 是"换序"。
/// 少了"拖"这一路，容器就成了一个只能被动更新的显示条 —— 用户想不出新按钮从哪来。
/// 旁边的「工具栏」那一页落点同时接这两种，见 <see cref="ToolbarLayoutStrip"/>。
/// </para>
/// <para>
/// 行数是<b>元数据表决定的</b>，不随用户数据增减 —— 目录不是清单，不该因为用户还没加就少一格。
/// </para>
/// </summary>
internal sealed class ToolbarToolLibrary
{
    /// <summary>拖动数据格式：拖的是<b>种类</b>（元数据那一层），不是某个实例。</summary>
    internal const string KindFormat = "LanStartWrite.ToolbarToolKind";

    private sealed class Tile
    {
        internal ToolbarToolKind Kind;
        internal Grid Container = null!;
        internal TextBlock Name = null!;
        internal TextBlock Description = null!;
        internal Border Badge = null!;
        internal TextBlock BadgeText = null!;
        internal Button Add = null!;
    }

    private readonly Panel _host;
    private readonly List<Tile> _tiles = [];

    /// <summary>
    /// 拖动落点那一排。库里的格把拖动"借"给它 —— <b>落点与落点提示只有那一排会算</b>，
    /// 两边各算一套就必然有两套"第几格"。
    /// </summary>
    private readonly ToolbarLayoutStrip? _strip;

    internal ToolbarToolLibrary(Panel host, ToolbarLayoutStrip? strip = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _strip = strip;
        Build();
    }

    /// <summary>库里的格数（探针与"元数据表漏了一类"的守卫看它）。</summary>
    internal int TileCount => _tiles.Count;

    /// <summary>取某一格上的加号（探针按种类取）。</summary>
    internal Button? FindAddButton(ToolbarToolKind kind) =>
        _tiles.FirstOrDefault(tile => tile.Kind == kind)?.Add;

    /// <summary>取某一格的整块（拖动源）。</summary>
    internal FrameworkElement? FindTile(ToolbarToolKind kind) =>
        _tiles.FirstOrDefault(tile => tile.Kind == kind)?.Container;

    /// <summary>那一格现在标着"已在工具栏上"吗（读的是真实数据，不是建表时的快照）。</summary>
    internal bool IsMarkedOnToolbar(ToolbarToolKind kind) =>
        _tiles.FirstOrDefault(tile => tile.Kind == kind) is { } tile && tile.BadgeText.Text.Length > 0;

    /// <summary>按当前数据刷新（只有"在不在工具栏上"这一条会变）。</summary>
    internal void Sync()
    {
        foreach (var tile in _tiles) Refresh(tile);
    }

    private void Build()
    {
        _host.Children.Clear();
        foreach (var entry in ToolbarToolCatalog.Entries)
        {
            var tile = BuildTile(entry);
            _tiles.Add(tile);
            _host.Children.Add(tile.Container);
        }
    }

    private Tile BuildTile(ToolbarToolCatalogEntry entry)
    {
        var tile = new Tile { Kind = entry.Kind };

        var glyph = ToolbarToolCatalog.Icon(entry, 20);
        var icon = new ContentControl
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        IconInk.Apply(icon, glyph);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(FrameworkElement.StyleProperty, "BodyTextBlockStyle");
        tile.Name = name;

        // 「已在工具栏上」那一枚角标：不是把整行灰掉 —— 灰掉看着像"这一类不能用了"，
        // 而事实是"这一类已经有了，还能再加"。
        var badgeText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        badgeText.SetResourceReference(FrameworkElement.StyleProperty, "HelperTextStyle");
        tile.BadgeText = badgeText;
        tile.Badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Child = badgeText,
        };
        tile.Badge.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        title.Children.Add(name);
        title.Children.Add(tile.Badge);

        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(icon);
        head.Children.Add(title);

        // 描述在名字<b>下方</b>、与图标左边线对齐 —— 图标那一列不参与对齐，
        // 否则描述会从图标底下开始，念起来像是在说图标。
        var description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(34, 4, 0, 0),
        };
        description.SetResourceReference(FrameworkElement.StyleProperty, "HelperTextStyle");
        tile.Description = description;

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(description);

        // 宽度给死：WrapPanel 按最大那一格排版，宽度不定的话每格随内容抖，
        // 整片网格的列宽就跟着抖 —— 而"对齐"正是网格该给的东西。
        var plus = new Button
        {
            Content = new FontIcon
            {
                Glyph = "", // E712 Add，取自库实测表
                FontFamily = "Segoe Fluent Icons",
                FontSize = 14,
            },
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        plus.SetResourceReference(FrameworkElement.StyleProperty, "ToolRowButtonStyle");
        var addedKind = entry.Kind;
        plus.Click += (_, _) => AddKind(addedKind, insertAt: null);
        AutomationProperties.SetName(plus, $"把{entry.Name}加到工具栏末尾");
        tile.Add = plus;

        tile.Container = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        tile.Container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tile.Container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bodyBorder = new Border
        {
            Width = 224,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Background = null,
            Child = body,
        };
        bodyBorder.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        tile.Container.Children.Add(bodyBorder);
        Grid.SetColumn(plus, 1);
        tile.Container.Children.Add(plus);

        var kind = entry.Kind;
        var canAdd = entry.Repeatable;
        tile.Container.Opacity = canAdd ? 1.0 : 0.75;

        // 库里的格是**拖动源**，走的是同一套进程内手势（阈值 3 DIP）——
        // 不是 <c>DragDrop.DoDragDrop</c>，理由见 InPlaceDragGesture 的类注释。
        // 固定项那一格**根本不给装手势**：它本来就有一枚，拖上来就是第二枚，
        // 而"拖不动"与"加号是灰的"必须说的是同一件事。
        if (canAdd)
        {
            _ = new InPlaceDragGesture(
                tile.Container,
                onPressed: () => { },
                onDragStarted: () => _strip?.BeginLibraryDrag(kind),
                onDragMoved: (_, now) => _strip?.UpdateDropTarget(tile.Container, now),
                onDragEnded: now => _strip?.CommitDrop(tile.Container, now),
                onCancelled: () => _strip?.CancelDrop());
        }

        AutomationProperties.SetName(tile.Container, $"{entry.Name}：{entry.Description}");

        Refresh(tile, entry);
        return tile;
    }

    /// <summary>
    /// 加一项，<b>并顺手选中它</b>：接下来几乎一定要调它的颜色 / 粗细，
    /// 不选中就变成"加了一支笔，然后不知道该改哪一支"。
    /// </summary>
    /// <param name="kind">哪一类。</param>
    /// <param name="insertAt">
    /// 落在第几格。<b>拖进来时给落点，点加号时给 null</b>（走"按老规矩插在选中那项后面"）。
    /// 两条路都在，是因为拖给"我知道我要放哪"的人，加号给"我就想再加一个"的人 ——
    /// 只留拖的话，另一类人每次都得先想好落在哪。
    /// </param>
    private static void AddKind(ToolbarToolKind kind, int? insertAt)
    {
        if (!ToolbarToolCatalog.For(kind).Repeatable) return;
        var added = ToolbarTools.Add(kind, insertAt);
        if (added is not null) ToolbarTools.Select(added.Id);
    }

    private static void Refresh(Tile tile, ToolbarToolCatalogEntry? entry = null)
    {
        entry ??= ToolbarToolCatalog.For(tile.Kind);

        tile.Name.Text = entry.Name;
        tile.Description.Text = entry.Description;

        var onToolbar = ToolbarToolCatalog.IsOnToolbar(entry.Kind);
        var count = ToolbarTools.Items.Count(tool => tool.Kind == entry.Kind);
        tile.BadgeText.Text = onToolbar ? (entry.Repeatable ? $"已放 {count} 个" : "已放 1 个") : string.Empty;
        tile.Badge.Visibility = onToolbar ? Visibility.Visible : Visibility.Collapsed;

        // 固定项的加号是灰的，并说清为什么：给一颗按了没反应的加号，
        // 用户会以为是自己操作错了（或者这格坏了），而不是"这一类本来就只有一枚"。
        tile.Add.IsEnabled = entry.Repeatable;
        AutomationProperties.SetName(tile.Add,
            entry.Repeatable ? $"把{entry.Name}加到工具栏末尾" : $"{entry.Name}是固定项，不能再加");
    }
}
