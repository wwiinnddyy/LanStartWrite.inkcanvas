using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 设置页里的「工具栏按钮」列表：一行一项，可以选中、上下移动、删除，末端有三个"再加一个"。
/// <para>
/// 与 <see cref="StrokeTipEditor"/> 同一个路子 —— 界面由数据生成，行数不写死。
/// 但这里多一件事：<b>行的容器只在结构变时重建</b>（增删换序），
/// 改数据（换色、拖粗细）只刷新那一行的图标与摘要。
/// 理由是同一行里还嵌着四颗按钮，重建会把键盘焦点丢掉 —— 而拖粗细滑杆时每一拍都会走到这条路上。
/// </para>
/// </summary>
internal sealed class ToolbarToolListEditor
{
    private sealed class RowView
    {
        internal string Id = "";
        internal Grid Container = null!;
        internal ContentPresenter IconHost = null!;
        internal TextBlock Title = null!;
        internal TextBlock Summary = null!;
        internal Button Use = null!;
        internal Button MoveUp = null!;
        internal Button MoveDown = null!;
        internal Button Remove = null!;
    }

    private readonly Panel _host;
    private readonly List<RowView> _rows = [];
    private string _signature = string.Empty;

    internal ToolbarToolListEditor(Panel host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>当前渲染出来的行数。断言与"数据加了项而列表没跟上"的守卫看它。</summary>
    internal int RowCount => _rows.Count;

    /// <summary>按显示顺序的项标识。</summary>
    internal IReadOnlyList<string> RowIds => [.. _rows.Select(static row => row.Id)];

    /// <summary>
    /// 取某一行里的一颗按钮，<paramref name="role"/> 取 <c>use</c> / <c>up</c> / <c>down</c> / <c>remove</c>。
    /// 按钮是代码建出来的、没有 <c>x:Name</c> 可查 —— 探针走这个入口。
    /// </summary>
    internal Button? FindRowButton(string toolId, string role)
    {
        foreach (var row in _rows)
        {
            if (!string.Equals(row.Id, toolId, StringComparison.Ordinal)) continue;
            return role switch
            {
                "use" => row.Use,
                "up" => row.MoveUp,
                "down" => row.MoveDown,
                "remove" => row.Remove,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>按当前数据刷新列表。结构没变就只刷外观（见类注释）。</summary>
    internal void Sync()
    {
        var signature = Signature();
        if (!string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            Build(signature);
            return;
        }

        for (var i = 0; i < _rows.Count && i < ToolbarTools.Items.Count; i++)
            RefreshVisuals(_rows[i], ToolbarTools.Items[i]);
    }

    private static string Signature() =>
        string.Join('|', ToolbarTools.Items.Select(static tool => $"{tool.Id}:{(int)tool.Kind}:{tool.Name}"));

    private void Build(string signature)
    {
        _signature = signature;
        _rows.Clear();
        _host.Children.Clear();

        foreach (var tool in ToolbarTools.Items)
        {
            var row = BuildRow(tool);
            _rows.Add(row);
            _host.Children.Add(row.Container);
        }
    }

    private RowView BuildRow(ToolbarTool tool)
    {
        var row = new RowView { Id = tool.Id };

        row.IconHost = new ContentPresenter
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        row.Title.SetResourceReference(FrameworkElement.StyleProperty, "BodyTextBlockStyle");

        row.Summary = new TextBlock();
        row.Summary.SetResourceReference(FrameworkElement.StyleProperty, "HelperTextStyle");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(row.Title);
        text.Children.Add(row.Summary);

        var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(row.IconHost);
        identity.Children.Add(text);

        var id = tool.Id;
        row.Use = RowButton("用这支", () => ToolbarTools.Select(id));
        row.MoveUp = RowButton("上移", () => ToolbarTools.Move(id, -1), new Thickness(8, 0, 0, 0));
        row.MoveDown = RowButton("下移", () => ToolbarTools.Move(id, 1));
        row.Remove = RowButton("删除", () => ToolbarTools.Remove(id));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(row.Use);
        actions.Children.Add(row.MoveUp);
        actions.Children.Add(row.MoveDown);
        actions.Children.Add(row.Remove);

        // Grid 而不是 StackPanel：文字列吃掉剩余宽度、按钮列贴右 ——
        // 一排行看下来按钮是对齐的，而不是各按各的文字长度错开。
        row.Container = new Grid { Margin = new Thickness(16, 8, 16, 8) };
        row.Container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Container.Children.Add(identity);
        Grid.SetColumn(actions, 1);
        row.Container.Children.Add(actions);

        RefreshVisuals(row, tool);
        return row;
    }

    private static Button RowButton(string label, Action click, Thickness? margin = null)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(FrameworkElement.StyleProperty, "ToolRowButtonStyle");
        if (margin.HasValue) button.Margin = margin.Value;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static void RefreshVisuals(RowView row, ToolbarTool tool)
    {
        row.IconHost.Content = ToolbarToolVisuals.Icon(tool, 16);
        row.Title.Text = tool.Name;
        row.Summary.Text = ToolbarTools.Describe(tool);

        // "用这支"对固定项（鼠标 / 撤销 / 重做 / 设置）与分隔线没有意义，对已选中的那一项更没有。
        var selected = string.Equals(ToolbarTools.SelectedId, tool.Id, StringComparison.Ordinal);
        var selectable = tool.Kind is ToolbarToolKind.Pen or ToolbarToolKind.Eraser or ToolbarToolKind.Mouse;
        row.Use.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
        row.Use.IsEnabled = selectable && !selected;
        row.Use.Content = selected ? "已选中" : "用这支";

        row.Remove.IsEnabled = tool.IsEditable;
        row.Remove.ToolTip = tool.IsEditable
            ? "删除这一项"
            : "鼠标模式、撤销、重做、设置是固定项：可以移动，不能删除";

        var index = ToolbarTools.IndexOf(tool.Id);
        row.MoveUp.IsEnabled = index > 0;
        row.MoveDown.IsEnabled = index >= 0 && index < ToolbarTools.Items.Count - 1;

        AutomationProperties.SetName(row.Container, $"{tool.Name}：{row.Summary.Text}");
    }
}
