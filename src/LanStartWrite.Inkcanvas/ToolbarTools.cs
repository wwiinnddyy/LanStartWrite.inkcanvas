using Dusk.Ink.Input;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 工具栏的<b>数据模型</b>：有哪些按钮、按什么顺序、各自带什么数据、当前选中哪一个。
/// <para>
/// <b>它是"工具栏可自定义"的全部落点。</b> 在这之前，工具栏的六颗钮是写死在标记里的，
/// 数据也只有一个全局副本（一支笔的颜色 / 粗细 / 笔型 + 一把橡皮的擦法 / 半径）——
/// 于是"再来一支红笔"这件事在数据上无处安放。
/// 现在：<b>按钮是一个列表，列表里每一项自带宽高颜色笔锋</b>，
/// 于是"两个笔按钮、数据各归各的"不需要任何新机制，它就是两条记录。
/// </para>
/// <para>
/// <b>与笔锋的关系</b>：<see cref="InkTipOptions"/> 仍然是"当前生效的那一份笔锋参数"
/// （画布只认它，设置页也只改它）。选中一支笔时本类把<b>那支笔存的笔锋</b>推给它；
/// 反过来用户调了参数，本类再把当前参数写回<b>选中的那支笔</b>。
/// 一来一回之后，两支笔各有各的形状，互不覆盖 —— 如果只存"档位标识"，
/// 手调的参数就会在两支笔之间串味。
/// </para>
/// <para>
/// <b>线程</b>：与偏好设置一致，只在 UI 线程上访问。
/// </para>
/// </summary>
internal static class ToolbarTools
{
    /// <summary>工具栏最多几项。存档是要被反复读写的，不能无界长。</summary>
    internal const int MaxItems = 24;

    private static readonly List<ToolbarTool> Tools = [];

    private static string _selectedId = string.Empty;
    private static int _idSeed;
    private static bool _loading;

    /// <summary>项列表变了（增、删、移动、或某一项的数据变了）。</summary>
    internal static event Action? LayoutChanged;

    /// <summary>当前选中的按钮换了。宿主据此把它那套数据应用到画布。</summary>
    internal static event Action? SelectionChanged;

    internal static IReadOnlyList<ToolbarTool> Items => Tools;

    internal static string SelectedId => _selectedId;

    internal static ToolbarTool? Selected => Find(_selectedId);

    static ToolbarTools()
    {
        // 参数变化 → 写回选中的那支笔。放在这里而不是各窗口里：写回是数据模型的事，
        // 而"谁改了参数"（设置页的滑杆、笔菜单的档位、读档）有三个来源。
        InkTipOptions.Changed += CaptureTipIntoSelectedTool;
    }

    /// <summary>按标识取一项；查不到返回 <c>null</c>（坏标识是常态，不是异常）。</summary>
    internal static ToolbarTool? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var tool in Tools)
        {
            if (string.Equals(tool.Id, id, StringComparison.Ordinal)) return tool;
        }

        return null;
    }

    /// <summary>某一项在列表里的位置（0 = 最左）；没有这一项返回 -1。</summary>
    internal static int IndexOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (var i = 0; i < Tools.Count; i++)
        {
            if (string.Equals(Tools[i].Id, id, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    /// <summary>当前列表的可序列化形态，交给存档。</summary>
    internal static IReadOnlyList<ToolbarTool> Snapshot() => [.. Tools];

    /// <summary>
    /// 从存档读入（启动时一次，或偏好变更时）。
    /// <para>
    /// 顺序很讲究：<b>先装列表、再定选中项</b> —— 选中项会触发"把它的笔锋推给画布"，
    /// 而那一刻列表必须已经就位，否则推的是一支已经不存在的笔。
    /// </para>
    /// </summary>
    internal static void Load(IReadOnlyList<ToolbarTool> items, string selectedId)
    {
        var source = items.Count > 0 ? items : DefaultItems();
        var resolvedSelected = source.Any(tool => string.Equals(tool.Id, selectedId, StringComparison.Ordinal))
            ? selectedId
            : string.Empty;
        if (resolvedSelected.Length == 0) resolvedSelected = FirstSelectable([.. source])?.Id ?? string.Empty;

        // 什么都没变就别动：这个方法会被每一次偏好变更调到（存个主题、拖个粗细都会），
        // 而它一旦往下走就会发两个事件，宿主会跟着把工具重新应用到画布上一遍。
        if (SameItems(Tools, source) && string.Equals(_selectedId, resolvedSelected, StringComparison.Ordinal)) return;

        _loading = true;
        try
        {
            Tools.Clear();
            Tools.AddRange(source);
            _idSeed = Math.Max(_idSeed, NextSeed(Tools));
            _selectedId = resolvedSelected;
        }
        finally { _loading = false; }

        // 读档之后要把选中那支笔的笔锋推给画布（这一步在 _loading 之外，才会真的发通知）。
        ApplySelectedTipToEngine();
        LayoutChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>选中一个按钮。<b>这一步把"当前工具"从一支笔换成另一支</b>，包括它的笔锋。</summary>
    internal static bool Select(string? id)
    {
        var tool = Find(id);
        if (tool is null || tool.Kind == ToolbarToolKind.Separator) return false;
        if (string.Equals(_selectedId, tool.Id, StringComparison.Ordinal)) return false;

        _selectedId = tool.Id;
        ApplySelectedTipToEngine();
        SelectionChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 添一项。<b>新项复制当前选中那一项的数据</b>（颜色、粗细、笔锋全都带上），
    /// 因为用户说的"再放一个"几乎总是"再放一个跟这个差不多的"；
    /// 空白的第二支笔只会让人再调一遍。
    /// </summary>
    internal static ToolbarTool? Add(ToolbarToolKind kind)
    {
        if (kind is not (ToolbarToolKind.Pen or ToolbarToolKind.Eraser or ToolbarToolKind.Separator))
            return null;
        if (Tools.Count >= MaxItems) return null;

        var template = Selected;
        var tool = kind switch
        {
            ToolbarToolKind.Pen => (template is { Kind: ToolbarToolKind.Pen } pen ? pen : DefaultPen()) with
            {
                Id = NextId("pen"),
                Name = NextName("笔", ToolbarToolKind.Pen),
            },
            ToolbarToolKind.Eraser => (template is { Kind: ToolbarToolKind.Eraser } eraser ? eraser : DefaultEraser()) with
            {
                Id = NextId("eraser"),
                Name = NextName("橡皮", ToolbarToolKind.Eraser),
            },
            _ => new ToolbarTool { Id = NextId("sep"), Kind = ToolbarToolKind.Separator, Name = "分隔线" },
        };

        tool = Normalize(tool);
        Tools.Insert(InsertIndex(), tool);
        LayoutChanged?.Invoke();
        return tool;
    }

    /// <summary>删一项。<b>鼠标模式与三个系统钮不在可删之列</b>（见 <see cref="ToolbarTool.IsEditable"/>）。</summary>
    internal static bool Remove(string? id)
    {
        var tool = Find(id);
        if (tool is null || !tool.IsEditable) return false;

        Tools.Remove(tool);
        if (string.Equals(_selectedId, tool.Id, StringComparison.Ordinal))
        {
            _selectedId = FirstSelectable(Tools)?.Id ?? string.Empty;
            ApplySelectedTipToEngine();
            SelectionChanged?.Invoke();
        }

        LayoutChanged?.Invoke();
        return true;
    }

    /// <summary>在列表里挪一格。<paramref name="delta"/> 为 -1 向前、+1 向后；到边界就是没挪动。</summary>
    internal static bool Move(string? id, int delta)
    {
        var tool = Find(id);
        if (tool is null || delta == 0) return false;

        var index = Tools.IndexOf(tool);
        var target = index + delta;
        if (target < 0 || target >= Tools.Count) return false;

        Tools.RemoveAt(index);
        Tools.Insert(target, tool);
        LayoutChanged?.Invoke();
        return true;
    }

    /// <summary>改某一项的数据。空改动（结果与原来相等）不会发通知。</summary>
    internal static bool Update(string id, Func<ToolbarTool, ToolbarTool> edit)
    {
        var tool = Find(id);
        if (tool is null) return false;

        // 走一遍规范化再比：**坏值不许进内存里那份活列表**。
        // 只在存档那一侧洗是不够的 —— 那样一个 NaN 粗细会留在运行时，
        // 一路走到画布上（而存档里存的却是洗过的 4，两边对不上）。
        var updated = Normalize(edit(tool) with { Id = tool.Id });
        if (updated == tool) return false;

        Tools[Tools.IndexOf(tool)] = updated;
        LayoutChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 改<b>当前选中那支笔</b>的数据；选中的不是笔就什么都不做。
    /// 设置页的粗细滑杆与笔菜单都走这一条 —— 于是"改的是哪一支笔"这个问题只有一个答案。
    /// </summary>
    internal static bool UpdateSelectedPen(Func<ToolbarTool, ToolbarTool> edit) =>
        Selected is { Kind: ToolbarToolKind.Pen } pen && Update(pen.Id, edit);

    internal static bool UpdateSelectedPenColor(uint color)
    {
        var scene = CanvasSceneState.Active;
        return Selected is { Kind: ToolbarToolKind.Pen } pen && Update(pen.Id, tool => scene == CanvasScene.Whiteboard
            ? tool with { WhiteboardColorArgb = color }
            : tool with { ScreenAnnotationColorArgb = color });
    }

    /// <summary>改<b>当前选中那把橡皮</b>的数据；选中的不是橡皮就什么都不做。</summary>
    internal static bool UpdateSelectedEraser(Func<ToolbarTool, ToolbarTool> edit) =>
        Selected is { Kind: ToolbarToolKind.Eraser } eraser && Update(eraser.Id, edit);

    /// <summary>
    /// 一行摘要，给设置页那个列表用：<c>书写笔 · 红色 · 4 px · 毛笔</c>。
    /// 工具栏上只有 40×40 一格，说不清"这支笔到底是什么"，摘要因此是必需的。
    /// </summary>
    internal static string Describe(ToolbarTool tool)
    {
        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
            {
                var parts = new List<string>
                {
                    PenKindName(tool.PenKind),
                    InkPalette.NearestName(tool.ColorFor(CanvasSceneState.Active)),
                    $"{Math.Round(tool.Thickness):0} px",
                };
                if (!tool.TipEnabled) parts.Add("无笔锋");
                else parts.Add(InkTipOptions.FindPreset(tool.TipPresetId)?.DisplayName ?? "手调笔锋");
                return string.Join(" · ", parts);
            }

            case ToolbarToolKind.Eraser:
                return tool.EraseMode == EraserMode.Area
                    ? $"面积擦 · 半径 {(int)Math.Round(tool.EraserRadius)} px"
                    : "笔迹擦 · 整笔摘除";

            default:
                return tool.Kind == ToolbarToolKind.Separator ? "分隔线" : tool.Name;
        }
    }

    internal static string PenKindName(PenKind kind) => kind switch
    {
        PenKind.Highlighter => "荧光笔",
        PenKind.Laser => "激光笔",
        _ => "书写笔",
    };

    // ------------------------------------------------------------------ 规范化

    /// <summary>用户可见名的长度上限。</summary>
    private const int MaxToolNameLength = 24;

    /// <summary>档位标识的长度上限（它只是个名字，长成这样就是坏数据）。</summary>
    private const int MaxTipPresetIdLength = 64;

    /// <summary>
    /// 把一项拉回合法形状。<b>这是唯一一处规范化</b>：文件的读取侧（<c>AppPreferences.ValidateTools</c>）
    /// 与模型的写入侧（<see cref="Update"/>）都走它。
    /// <para>
    /// 两处都用同一份规则不是"顺手"：只在存档那一侧洗，内存里那份活列表就会留着坏值 ——
    /// 一个 NaN 粗细会一路走到画布上，而存档里存的却是洗过的默认值，两边对不上且看不出来。
    /// </para>
    /// <para>
    /// <b>与类型无关的字段一律清回默认值</b>：分隔线身上不该留着颜色，
    /// 否则存档会被后人误读成"这个按钮也有颜色"。
    /// </para>
    /// </summary>
    internal static ToolbarTool Normalize(ToolbarTool tool)
    {
        var name = (tool.Name ?? string.Empty).Trim();
        if (name.Length > MaxToolNameLength) name = name[..MaxToolNameLength];
        var legacyColor = tool.ColorArgb == 0xFF00B7C3
            ? Argb.Pack(InkPalette.Colors[5])
            : tool.ColorArgb;

        switch (tool.Kind)
        {
            case ToolbarToolKind.Pen:
                return tool with
                {
                    Name = name.Length > 0 ? name : "笔",
                    ColorArgb = ToolbarTool.DefaultColorArgb,
                    ScreenAnnotationColorArgb = tool.ScreenAnnotationColorArgb ?? legacyColor,
                    WhiteboardColorArgb = tool.WhiteboardColorArgb ?? legacyColor,
                    Thickness = double.IsFinite(tool.Thickness)
                        ? Math.Clamp(Math.Round(tool.Thickness), 1, 24)
                        : 4,
                    PenKind = Enum.IsDefined(tool.PenKind) ? tool.PenKind : PenKind.Pen,
                    TipPresetId = Trim(tool.TipPresetId, MaxTipPresetIdLength),
                    TipValues = NormalizeTipValues(tool.TipValues),
                };

            case ToolbarToolKind.Eraser:
                return tool with
                {
                    Name = name.Length > 0 ? name : "橡皮",
                    EraseMode = Enum.IsDefined(tool.EraseMode) ? tool.EraseMode : EraserMode.Area,
                    EraserRadius = double.IsFinite(tool.EraserRadius)
                        ? Math.Clamp(Math.Round(tool.EraserRadius), 4, 48)
                        : 14,
                };

            default:
                return new ToolbarTool
                {
                    Id = tool.Id,
                    Kind = tool.Kind,
                    Name = name.Length > 0 ? name : DefaultNameFor(tool.Kind),
                };
        }
    }

    /// <summary>
    /// 把一支笔的笔锋取值拉回参数表的形状：长度按<b>本表</b>对齐（短了补默认、长了截掉），
    /// 每一项钳到自己的区间，非有限值退回该参数的默认值。<c>null</c> 原样返回（表示"跟着档位走"）。
    /// </summary>
    private static TipValueVector? NormalizeTipValues(TipValueVector? vector)
    {
        if (vector?.Values is not { Length: > 0 } stored) return null;

        var parameters = StrokeTipParameters.All;
        var values = new double[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var raw = i < stored.Length ? stored[i] : parameters[i].DefaultValue;
            values[i] = double.IsFinite(raw)
                ? Math.Clamp(raw, parameters[i].Minimum, parameters[i].Maximum)
                : parameters[i].DefaultValue;
        }

        return new TipValueVector { Values = values };
    }

    private static string DefaultNameFor(ToolbarToolKind kind) => kind switch
    {
        ToolbarToolKind.Mouse => "鼠标模式",
        ToolbarToolKind.Pen => "笔",
        ToolbarToolKind.Eraser => "橡皮",
        ToolbarToolKind.Undo => "撤销",
        ToolbarToolKind.Redo => "重做",
        ToolbarToolKind.Settings => "设置",
        ToolbarToolKind.Whiteboard => "白板",
        _ => "分隔线",
    };

    private static string Trim(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 默认工具栏 = 今天这一条：鼠标 / 白板 / 笔 / 橡皮 / 撤销 / 重做 / 分隔 / 设置。
    /// <para>
    /// 白板紧跟在鼠标后面：这两颗是一对（一个出这块画布、一个进那块）。
    /// 存档里补齐固定项时用的是同一个位置，所以"首启"与"从旧档升级"看到的是同一条顺序。
    /// </para>
    /// </summary>
    internal static List<ToolbarTool> DefaultItems() =>
    [
        new ToolbarTool { Id = "mouse", Kind = ToolbarToolKind.Mouse, Name = "鼠标模式" },
        new ToolbarTool { Id = "whiteboard", Kind = ToolbarToolKind.Whiteboard, Name = "白板" },
        DefaultPen() with { Id = "pen.1", Name = "笔" },
        DefaultEraser() with { Id = "eraser.1", Name = "橡皮" },
        new ToolbarTool { Id = "undo", Kind = ToolbarToolKind.Undo, Name = "撤销" },
        new ToolbarTool { Id = "redo", Kind = ToolbarToolKind.Redo, Name = "重做" },
        new ToolbarTool { Id = "sep.1", Kind = ToolbarToolKind.Separator, Name = "分隔线" },
        new ToolbarTool { Id = "settings", Kind = ToolbarToolKind.Settings, Name = "设置" },
    ];

    internal static ToolbarTool DefaultPen()
    {
        // 起始形状取「标准」档那一组值，并且<b>存下来</b>（而不是留 null 表示"跟着档位走"）：
        // 一支笔的形状是它自己的属性，显式存下来之后，日后改了某一档的定义不会悄悄改掉已经用起来的笔。
        var standard = StrokeTipPresetLibrary.Default.Find("standard");

        return new ToolbarTool
        {
            Kind = ToolbarToolKind.Pen,
            Name = "笔",
            ColorArgb = ToolbarTool.DefaultColorArgb,
            ScreenAnnotationColorArgb = ToolbarTool.DefaultColorArgb,
            WhiteboardColorArgb = ToolbarTool.DefaultColorArgb,
            Thickness = 4,
            PenKind = PenKind.Pen,
            TipPresetId = "standard",
            TipEnabled = true,
            TipValues = standard is null ? null : new TipValueVector { Values = [.. standard.Values] },
        };
    }

    internal static ToolbarTool DefaultEraser() => new()
    {
        Kind = ToolbarToolKind.Eraser,
        Name = "橡皮",
        EraseMode = EraserMode.Area,
        EraserRadius = 14,
    };

    /// <summary>把选中那支笔的笔锋推给引擎（只有笔要推；橡皮与别的项不动笔锋）。</summary>
    private static void ApplySelectedTipToEngine()
    {
        if (_loading) return;
        if (Selected is not { Kind: ToolbarToolKind.Pen } pen) return;

        // 压住"写回"：这一趟是<b>读</b>那支笔的参数，不是用户改参数。
        // 不压的话，每选一次"跟着档位走"的笔，都会顺手把它物化成一份显式拷贝 ——
        // 于是"改了预设定义、引用它的笔跟着变"这条性质就没了，而且存档会白涨一圈。
        // 注意这里压的只是写回，<b>引擎那边的通知照发</b>：画布就是靠它更新 TipSettings 的。
        _loading = true;
        try { InkTipOptions.LoadState(pen.TipPresetId, pen.TipEnabled, pen.TipValues?.Values); }
        finally { _loading = false; }
    }

    /// <summary>
    /// 引擎那边的笔锋参数变了 → 写回<b>选中的那支笔</b>。
    /// <para>
    /// 没有这一步，"两支笔数据独立"就是假的：设置页改的是当前那一份参数，
    /// 而它有唯一的归属 —— 不写回去，切走再切回来就丢了。
    /// </para>
    /// </summary>
    private static void CaptureTipIntoSelectedTool()
    {
        if (_loading) return;
        if (Selected is not { Kind: ToolbarToolKind.Pen } pen) return;

        Update(pen.Id, tool => tool with
        {
            TipPresetId = InkTipOptions.PresetId,
            TipEnabled = InkTipOptions.Enabled,
            TipValues = new TipValueVector { Values = InkTipOptions.CurrentValues },
        });
    }

    private static ToolbarTool? FirstSelectable(List<ToolbarTool> tools)
    {
        foreach (var tool in tools)
        {
            if (tool.Kind is ToolbarToolKind.Pen or ToolbarToolKind.Eraser or ToolbarToolKind.Mouse) return tool;
        }

        return tools.Count > 0 ? tools[0] : null;
    }

    private static bool SameItems(List<ToolbarTool> left, IReadOnlyList<ToolbarTool> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// 新项插在选中项<b>之后</b>（"再放一个"的自然位置）。选中的若是系统钮 —— 比如设置 ——
    /// 就插到第一颗系统钮之前，也就是绘制工具那一组的末尾，免得一笔把撤销和重做隔开。
    /// </summary>
    private static int InsertIndex()
    {
        var selected = Selected;
        if (selected is null) return 0;
        if (!selected.IsEditable)
        {
            var firstFixed = Tools.FindIndex(static tool => !tool.IsEditable);
            return firstFixed < 0 ? Tools.Count : firstFixed;
        }

        return Tools.IndexOf(selected) + 1;
    }

    private static string NextId(string prefix)
    {
        _idSeed++;
        var id = $"{prefix}.{_idSeed}";
        // 理论上撞不上（种子只增不减），但存档里可能有手写的同号项 —— 撞了就继续往后找。
        while (Find(id) is not null)
        {
            _idSeed++;
            id = $"{prefix}.{_idSeed}";
        }

        return id;
    }

    /// <summary>新项的名字：同类里第一个空号。<c>笔 / 笔 2 / 笔 3</c>，而不是一串"新笔"。</summary>
    private static string NextName(string baseName, ToolbarToolKind kind)
    {
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? baseName : $"{baseName} {index}";
            var taken = Tools.Exists(tool => tool.Kind == kind && string.Equals(tool.Name, candidate, StringComparison.Ordinal));
            if (!taken) return candidate;
        }
    }

    /// <summary>把所有 <c>名字.N</c> 里的 N 顶到最大值，这样接着新建不会与已有标识相撞。</summary>
    private static int NextSeed(List<ToolbarTool> tools)
    {
        var max = 0;
        foreach (var tool in tools)
        {
            var dot = tool.Id.LastIndexOf('.');
            if (dot < 0 || dot == tool.Id.Length - 1) continue;
            if (int.TryParse(tool.Id[(dot + 1)..], out var value)) max = Math.Max(max, value);
        }

        return max;
    }
}
