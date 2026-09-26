using FluentJalium.Controls;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 设置页「工具栏」那一页：<b>工具栏上此刻真正有的那些项，按显示顺序摆成一排</b>，
/// 每一项画成标签的形状。这一排同时是<b>拖放落点</b>与<b>拖动源</b>。
/// <para>
/// 抄的是 Class Island 的 <c>EditableComponentsListBox</c>：那边容器
/// <b>一个落点接两种拖</b> —— 从组件库拖 <c>ComponentInfo</c> 进来是"新建一个"，
/// 在列表里拖 <c>ComponentSettings</c> 是"换序"。两种走同一个 <c>ValidateCore</c>，
/// 靠 <c>DragEffects</c> 分流（Copy 新建 / Move 换序）。
/// 这里同构：抬手那一趟 <see cref="CommitDrop"/> 先问"这一趟拖的是种类还是已存在的项"。
/// </para>
/// <para>
/// 少了"从库拖进来"这一路，这一排就只是个被动更新的显示条 ——
/// 用户想不出新按钮从哪来，于是"自定义工具栏"这件事只做到了"删和换序"。
/// </para>
/// <para>
/// 行的容器仍然<b>只在结构变时重建</b>（增删换序），改数据只刷那一项的外观：
/// 拖动排版时每一拍都会走到这条路上，重建会把键盘焦点丢掉。
/// </para>
/// </summary>
internal sealed class ToolbarLayoutStrip
{
    /// <summary>
    /// 供组件库那一格把拖动"借"到这一排上：库拖过来的是<b>种类</b>（新建一件），
    /// 这一排里拖的是<b>项标识</b>（换序）。两条路在抬手那一刻靠这一个标志分流。
    /// <para>
    /// 标志挂在这一排上而不是全局：拖动状态是<b>这一排</b>的事（谁在被拖、落在哪一格），
    /// 放全局就变成"整个应用有一个当前拖动"，而那个东西只有这一排会读。
    /// </para>
    /// </summary>
    internal void BeginLibraryDrag(ToolbarToolKind kind) => _dragKind = kind;

    private sealed class Chip
    {
        internal string Id = "";
        internal Border Container = null!;
        internal Border Surface = null!;
        internal Border Selection = null!;

        /// <summary>拖动时画在那里的插入边缘（accent 粗边）。零布局影响，见 PaintInsertEdge。</summary>
        internal Border InsertEdge = null!;

        /// <summary>选中时才出现的那一排操作（Class Island 那边是 adorner on selected）。</summary>
        internal Panel Actions = null!;
        internal Button Remove = null!;
    }

    private readonly Panel _host;
    private readonly List<Chip> _chips = [];
    private string _signature = string.Empty;

    /// <summary>落点提示线：拖动时插在哪一格之前。</summary>
    /// <summary>当前落点（-1 = 不落在任何项之前）。</summary>
    internal int DropTargetIndex { get; private set; } = -1;

    /// <summary>
    /// 此刻是不是真的在拖（"只是按下了"不算）。
    /// <b>由手势真的开始那一刻起才为真</b>，所以拿它验"阈值之前不该开始拖"是有效的。
    /// </summary>
    internal bool IsDragging => _dragKind is not null || _dragToolId.Length > 0;

    internal ToolbarLayoutStrip(Panel host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>当前渲染出来的项数。断言与"数据变了而这一排没跟上"的守卫看它。</summary>
    internal int ChipCount => _chips.Count;

    /// <summary>按显示顺序的项标识。</summary>
    internal IReadOnlyList<string> ChipIds => [.. _chips.Select(static chip => chip.Id)];

    /// <summary>取某一项上的「×」。<b>固定项返回 null</b> —— 它压根没有这一颗，
    /// 而不是有一颗按了没反应（后者会让人以为删不掉是这个按钮坏了）。</summary>
    internal Button? FindChipButton(string toolId, string role) =>
        _chips.FirstOrDefault(chip => string.Equals(chip.Id, toolId, StringComparison.Ordinal)) is { } chip
            ? role == "remove" ? chip.Remove : null
            : null;

    /// <summary>取某一项的整块（拖放落点与"它真的排了版"那条断言要它）。</summary>
    internal FrameworkElement? FindChip(string toolId) =>
        _chips.FirstOrDefault(chip => string.Equals(chip.Id, toolId, StringComparison.Ordinal))?.Container;

    /// <summary>
    /// 那一块现在是不是"被选中"的样子。<b>读的是描边粗细这个真的画出去的事实</b>，
    /// 不是另记一个标志位 —— 那样"选中"就有了两个来源，而标志位与画出来的那件事迟早不一致。
    /// </summary>
    internal bool IsChipSelected(string toolId) =>
        _chips.FirstOrDefault(c => string.Equals(c.Id, toolId, StringComparison.Ordinal)) is { } chip
            && chip.Selection.BorderThickness.Left > 0;

    /// <summary>按当前数据刷新这一排。结构没变就只刷外观。</summary>
    internal void Sync()
    {
        var signature = Signature();
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            Build(signature);
            return;
        }

        for (var i = 0; i < _chips.Count && i < ToolbarTools.Items.Count; i++)
            RefreshVisuals(_chips[i], ToolbarTools.Items[i]);
    }

    private static string Signature() =>
        string.Join('|', ToolbarTools.Items.Select(static tool => $"{tool.Id}:{(int)tool.Kind}"));

    private void Build(string signature)
    {
        _signature = signature;
        _chips.Clear();
        _host.Children.Clear();
        DropTargetIndex = -1;

        foreach (var tool in ToolbarTools.Items)
        {
            var chip = BuildChip(tool);
            _chips.Add(chip);
            _host.Children.Add(chip.Container);
        }
    }

    private Chip BuildChip(ToolbarTool tool)
    {
        var chip = new Chip { Id = tool.Id };
        var catalog = ToolbarToolCatalog.For(tool.Kind);

        var glyph = ToolbarToolCatalog.Icon(catalog, 16);
        var icon = new ContentControl
        {
            Content = glyph,
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        IconInk.Apply(icon, glyph);

        var name = new TextBlock
        {
            Text = ToolbarToolVisuals.DisplayName(tool),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.SetResourceReference(FrameworkElement.StyleProperty, "BodyTextBlockStyle");

        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        head.Children.Add(name);

        // 操作条默认收起来，选中那一项才显示（Class Island 的 adorner on selected）。
        // 每一项都常驻按钮的话，一排十几个按钮全亮着，没有一处能看。
        var id = tool.Id;
        if (tool.IsEditable) chip.Remove = MakeIconButton("从工具栏移除", 0xE711, () => ToolbarTools.Remove(id));
        chip.Actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            // 收起来由 RefreshVisuals 按"选中没有"翻；这里给的是初值。
            Visibility = Visibility.Collapsed,
        };
        if (chip.Remove is { } remove) chip.Actions.Children.Add(remove);
        head.Children.Add(chip.Actions);

        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        body.Children.Add(icon);
        body.Children.Add(head);

        // 三层：容器（留白 + 落点判定）／选中描边（accent 那一圈）／表面（底色）。
        // 分三层是因为"选中"有两件事要同时说：底色换成实心 accent，以及外面套一圈描边。
        // 挤在一层里的话，描边会被底色盖住，而底色一变描边色也得跟着变 —— 两个真相。
        chip.Surface = new Border
        {
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Child = body,
        };
        chip.Surface.SetResourceReference(Border.BackgroundProperty, "TabViewItemHeaderBackground");

        chip.InsertEdge = new Border
        {
            // 3 DIP 宽、accent 色、默认收起来。它是那一项的<b>孩子</b>而不是面板的新成员，
            // 所以既不推动任何东西（Alignment 到左右边、宽度固定），也吃不到命中。
            Width = 3,
            Margin = new Thickness(2, 2, 2, 2),
            CornerRadius = new CornerRadius(2),
            Background = null,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        chip.InsertEdge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

        chip.Selection = new Border
        {
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            BorderThickness = new Thickness(1.5),
            BorderBrush = null,
            Child = new Grid
            {
                Children =
                {
                    chip.Surface,
                    chip.InsertEdge,
                },
            },
        };
        chip.Selection.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");

        chip.Container = new Border
        {
            Margin = new Thickness(0, 0, 2, 0),
            Child = chip.Selection,
        };
        chip.Container.Background = null;

        var toolId = tool.Id;
        // 选中与拖动**分开**：点一下是选中，按住拖才是拖动。
        // 早先那版在鼠标按下就起 DoDragDrop，于是两者分不开 ——
        // "选中这一项好调它的颜色"这条功能直接没了，而症状只是"点不动"。
        _ = new InPlaceDragGesture(
            chip.Container,
            onPressed: () => ToolbarTools.Select(toolId),
            onDragStarted: () => BeginChipDrag(chip),
            onDragMoved: (_, now) => UpdateDropTarget(chip.Container, now),
            onDragEnded: now => CommitDrop(chip.Container, now),
            onCancelled: ClearDropMarker);

        AutomationProperties.SetName(chip.Container, ToolbarTools.Describe(tool));

        RefreshVisuals(chip, tool);
        return chip;
    }

    private static Button MakeIconButton(string label, ushort glyph, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = ((char)glyph).ToString(),
                FontFamily = "Segoe Fluent Icons",
                FontSize = 10,
            },
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "ToolRowButtonStyle");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static void RefreshVisuals(Chip chip, ToolbarTool tool)
    {
        var selected = string.Equals(ToolbarTools.SelectedId, tool.Id, StringComparison.Ordinal);
        // 两个键都是库里 TabView 真实发布的那两个 —— 键名写错<b>不报错</b>，
        // 只是 SetResourceReference 什么也没做，于是整排标签没有底色（而"标签没有底色"看着还挺像设计）。
        chip.Surface.SetResourceReference(Border.BackgroundProperty,
            selected ? "TabViewItemHeaderBackgroundSelected" : "TabViewItemHeaderBackground");
        chip.Selection.BorderThickness = new Thickness(selected ? 1.5 : 0);
        chip.Actions.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(chip.Container, ToolbarTools.Describe(tool));
    }

    // ---------------------------------------------------------------- 拖动落点

    /// <summary>这一趟拖动带的是什么：<b>从组件库来的是种类（新建），从这一排来的是项标识（换序）。</b></summary>
    private ToolbarToolKind? _dragKind;

    private string _dragToolId = string.Empty;

    /// <summary>拖动开始：清掉上一趟留下的痕迹。</summary>
    private void BeginChipDrag(Chip chip)
    {
        _dragKind = null;
        _dragToolId = chip.Id;
        ShowDropMarker(chip.Container, new Point(0, 0));
    }

    /// <summary>这一趟拖动作废了（Esc / 丢了捕获）：清掉痕迹，不改数据。</summary>
    internal void CancelDrop()
    {
        _dragKind = null;
        _dragToolId = string.Empty;
        ClearDropMarker();
    }

    /// <summary>
    /// 手指（或这一排里的那一项）走到哪一格上了。<b>靠命中测试找落点</b>，不靠"当前那一项是谁"。
    /// </summary>
    internal void UpdateDropTarget(FrameworkElement? source, Point nowInWindow)
    {
        if (source is null) return;
        var window = InPlaceDragGesture.FindWindow(source);
        if (window is null) return;

        var inWindow = source.TranslatePoint(nowInWindow, window);
        var hit = window.HitTest(inWindow)?.VisualHit;
        var over = FindChipFrom(hit);
        if (over is null)
        {
            // 落在这一排之外（组件库之间的空隙、卡片上）= 不落在任何格之前。
            // 不这样处理的话，扫过组件库时落点会一直亮着，用户以为还能放。
            DropTargetIndex = -1;
            ClearDropMarker();
            return;
        }

        // 指针落在那一项里的横向偏移：**把那一项的左缘正向换到窗口坐标，再相减**。
        // 不走 <c>over.TranslatePoint(inWindow, over)</c> 那个反向换算 ——
        // 那个方向在这一套里不保证是 <c>TranslatePoint</c> 正向的逆，两次误差叠起来落点就偏一格，
        // 而症状只是"差一点点"。同向相减则只差一次。
        var chipLeft = over.TranslatePoint(new Point(0, 0), window).X;
        ShowDropMarker(over, new Point(inWindow.X - chipLeft, inWindow.Y));
    }

    internal void CommitDrop(FrameworkElement? source, Point nowInWindow)
    {
        var index = DropTargetIndex;
        var kind = _dragKind;
        var toolId = _dragToolId;
        ClearDropMarker();
        _dragKind = null;
        _dragToolId = string.Empty;
        if (index < 0) return;

        // 「从组件库来」= 新建一件；「从这一排来」= 换序。分流点就是这一行。
        if (kind is { } added)
        {
            var tool = ToolbarTools.Add(added, index);
            if (tool is not null) ToolbarTools.Select(tool.Id);
        }
        else
        {
            ToolbarTools.MoveTo(toolId, index);
        }
    }

    private FrameworkElement? FindChipFrom(object? hit)
    {
        DependencyObject? current = hit as DependencyObject;
        while (current is not null)
        {
            if (current is Border { } border && _chips.Any(chip => ReferenceEquals(chip.Container, border)))
                return border;
            current = current is Visual visual ? visual.VisualParent : null;
        }

        return null;
    }

    /// <summary>
    /// <summary>
    /// 落点：<b>落在这一项的左半边 = 插到它前面，右半边 = 插到它后面</b>。
    /// <para>
    /// 与 Class Island 的 <c>GetTargetIndex</c> 同一条判据（<c>rPos.X &lt;= width/2</c> 取
    /// <c>index - 1</c>，否则 <c>index</c>，再 <c>+1</c>）。两处都用"半边"而不是一个
    /// 插在中间的窄条：后者要求用户瞄一个只有几像素宽的位置，命中率低且毫无自解释性。
    /// </para>
    /// <para>
    /// <b>判据必须在那一项自己的坐标系里做</b>，而那个点来自命中测试
    /// （<c>HitTest</c> 拿到的就是元素内坐标）。早先那版把中点 <c>TranslatePoint</c> 到宿主坐标
    /// 再去比 <c>ActualWidth / 2</c> —— 两个不同的坐标系，于是"左半边 / 右半边"从来没生效：
    /// 算出来永远是"右半边"，症状是"拖到哪都插在后面"，完全看不出是坐标算错了。
    /// </para>
    /// </summary>
    private void ShowDropMarker(FrameworkElement target, Point inTarget)
    {
        var index = _chips.FindIndex(chip => ReferenceEquals(chip.Container, target));
        if (index < 0) { DropTargetIndex = -1; PaintInsertEdge(-1); return; }

        // 零宽（刚重建、还没排版）时按左半边处理 —— 那是不移动，
        // 而不是把东西放到用户没指的地方。
        DropTargetIndex = target.ActualWidth > 0 && inTarget.X >= target.ActualWidth / 2
            ? index + 1
            : index;
        PaintInsertEdge(DropTargetIndex);
    }

    /// <summary>
    /// 把"插在这里"画成<b>那一项自己的插入边缘</b>（accent 的一条粗边），而不是往面板里插一根线。
    /// <para>
    /// 往 <c>_host</c> 里插一根线是错的，而且错两次：
    /// <list type="bullet">
    /// <item>插入动作把后面所有项<b>整体推开</b>（线宽 + 边距 ≈ 4 DIP），
    /// 于是<b>指针底下的东西自己在动</b> —— 落点跟着漂一格，症状是"差一点点"。</item>
    /// <item>那根线正好落在指针所在的位置，可命中就把命中测试从那一项上抢走
    /// （走 <c>AllowDrop</c> 那条路时已经吃过一次这个亏）。</item>
    /// </list>
    /// 画在项<b>自己</b>的边框上：零布局影响，也不抢命中。
    /// </para>
    /// </summary>
    private void PaintInsertEdge(int insertAt)
    {
        for (var i = 0; i < _chips.Count; i++)
        {
            var chip = _chips[i];
            var before = i == insertAt;
            var after = insertAt >= _chips.Count && i == _chips.Count - 1;
            chip.InsertEdge.Visibility = before || after ? Visibility.Visible : Visibility.Collapsed;
            chip.InsertEdge.HorizontalAlignment = before ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
    }

    private void ClearDropMarker()
    {
        DropTargetIndex = -1;
        PaintInsertEdge(-1);
    }
}
