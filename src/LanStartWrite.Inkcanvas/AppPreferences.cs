using System.Diagnostics;
using System.Text.Json;
using Dusk.Ink.Input;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

internal enum AppTheme { Light, Dark, System }

/// <summary>
/// 打开一张图时，图片批注那块<b>以什么形状出现</b>。
/// <para>
/// 两种都要：窗口适合"边看边批注、同时还要看见别的窗口"，全屏适合"专心看这一张"。
/// 它是<b>图片这一块自己的</b>打开方式，与白板/屏幕批注那两块无关，所以不进
/// <see cref="CanvasSceneSettings"/> —— 那份是"每块画布该有怎样的表现"。
/// </para>
/// </summary>
internal enum ImageOpenMode
{
    /// <summary>一个可缩放的普通窗口，工具栏停靠在它正下方。</summary>
    Window = 0,

    /// <summary>占满整块屏幕，工具栏不跟着动（沿用白板那套浮在屏幕底部的行为）。</summary>
    FullScreen = 1,
}

/// <summary>
/// 数值向量的<b>按值比较</b>，给本文件里那几个进存档的类型共用。
/// <para>
/// 存在的理由只有一条：<see cref="PreferenceSnapshot"/> 是 record，合成的 <c>Equals</c>
/// 对数组 / 列表是按<b>引用</b>比的，于是"存盘再读回来应当相等"这条断言
/// （UiSmoke 的 <c>Preferences round-trip through JSON</c>）会在往返之后当场变红。
/// 把数组包成会按值比较的类型，那条断言就还是它原来的意思。
/// </para>
/// </summary>
internal static class TipValue
{
    internal static bool Same(IReadOnlyList<double>? left, IReadOnlyList<double>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!left[i].Equals(right[i])) return false;
        return true;
    }

    internal static int Hash(IReadOnlyList<double>? values)
    {
        if (values is null) return 0;
        var hash = new HashCode();
        for (var i = 0; i < values.Count; i++) hash.Add(values[i]);
        return hash.ToHashCode();
    }
}

/// <summary>一串按参数表顺序排列的笔锋取值。</summary>
/// <summary>
/// 「最近打开的图片」那份列表，<b>按值比较</b>。
/// <para>
/// 理由与 <see cref="TipPresetCollection"/> 一字不差：<see cref="PreferenceSnapshot"/> 是 record，
/// 合成的 <c>Equals</c> 对 <c>List&lt;T&gt;</c> 按<b>引用</b>比，而 UiSmoke 有一条
/// 「Preferences round-trip through JSON」断言它存盘再读回来仍然相等。
/// 裸放一个 <c>List&lt;string&gt;</c> 进去，那条断言会<b>永远</b>红 —— 而且红得莫名其妙：
/// 明明两条快照内容一模一样。这不是"测试太严"，是那份相等语义已经不再是它声称的东西。
/// </para>
/// </summary>
internal sealed record RecentImageCollection
{
    /// <summary>可空理由同 <see cref="TipValueVector.Values"/>：它从文件里来。</summary>
    public List<string>? Items { get; init; } = [];

    public bool Equals(RecentImageCollection? other)
    {
        if (other is null) return false;
        var mine = Items ?? [];
        var theirs = other.Items ?? [];
        if (mine.Count != theirs.Count) return false;
        for (int i = 0; i < mine.Count; i++)
            if (!string.Equals(mine[i], theirs[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items ?? []) hash.Add(item, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// 一串按参数表顺序排列的笔锋取值。
/// </summary>
internal sealed record TipValueVector
{
    /// <summary>
    /// 可空不是为了宽容调用方，而是因为<b>它从文件里来</b>：JSON 里写成 <c>"values": null</c>
    /// 会让反序列化直接把它置空。读取那一侧因此必须把 null 当"没有这一项"，
    /// 而不是让一个手改坏的存档把启动打崩。
    /// </summary>
    public double[]? Values { get; init; } = [];

    public bool Equals(TipValueVector? other) => other is not null && TipValue.Same(Values, other.Values);

    public override int GetHashCode() => TipValue.Hash(Values);
}

/// <summary>
/// 一条用户自己保存的笔锋预设：只有"叫什么"与"一组取值"，没有别的状态 ——
/// 它和引擎里的内置档位在数据上完全同构，这正是"我的笔锋"不需要新概念的原因。
/// </summary>
internal sealed record TipPresetRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>可空理由同 <see cref="TipValueVector.Values"/>。</summary>
    public double[]? Values { get; init; } = [];

    public bool Equals(TipPresetRecord? other) =>
        other is not null && Id == other.Id && Name == other.Name && TipValue.Same(Values, other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(TipValue.Hash(Values));
        return hash.ToHashCode();
    }
}

/// <summary>自定义预设的集合，同样是按值比较（理由见 <see cref="TipValue"/>）。</summary>
internal sealed record TipPresetCollection
{
    /// <summary>可空理由同 <see cref="TipValueVector.Values"/>。</summary>
    public List<TipPresetRecord>? Items { get; init; } = [];

    public bool Equals(TipPresetCollection? other)
    {
        if (other is null) return false;

        var mine = Items ?? [];
        var theirs = other.Items ?? [];
        if (mine.Count != theirs.Count) return false;
        for (var i = 0; i < mine.Count; i++)
            if (mine[i] != theirs[i]) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items ?? []) hash.Add(item);
        return hash.ToHashCode();
    }
}

internal sealed record PreferenceSnapshot
{
    public AppTheme Theme { get; init; } = AppTheme.Light;
    public bool ReduceMotion { get; init; }
    public bool KeepToolbarOnTop { get; init; } = true;

    /// <summary>
    /// 压力感应。<b>它是设备能力开关，不是某一支笔的属性</b>，所以留在这里而不是进工具栏项。
    /// </summary>
    public bool Pressure { get; init; }

    /// <summary>
    /// 工具栏上有哪些按钮、按什么顺序、各自带什么数据。
    /// <para>
    /// <b>画笔粗细 / 橡皮擦法 / 橡皮半径 / 笔锋取值都在这里面</b> —— 它们曾经是这份快照上的全局字段，
    /// 而"两个笔按钮各自一套数据"要求它们属于某一项。留一份全局的做镜像就是第二个真相，
    /// 所以那些字段是<b>删掉</b>而不是留着兼容。
    /// </para>
    /// </summary>
    public ToolbarToolCollection ToolbarItems { get; init; } = new();

    /// <summary>当前选中的那一项（下次启动还停在这支笔上）。列表里查不到时落空串。</summary>
    public string ToolbarSelectedId { get; init; } = "";

    /// <summary>用户保存的笔锋预设。<b>它是全应用共享的色板，不是某一支笔的属性</b>，所以不进工具栏项。</summary>
    public TipPresetCollection TipCustomPresets { get; init; } = new();

    /// <summary>
    /// 各场景的画布设置（穿透模式 / 冻结模式 / 白板底色）。
    /// <para>
    /// <b>按场景存</b>而不是一份全局的：屏幕批注要的是"透出桌面"，白板要的正好相反，
    /// 一套全局开关会让它们互相改。两个场景都已经真的存在了（<see cref="CanvasScene"/>），
    /// 再加一个场景仍然只动枚举与设置页，不动这里。
    /// </para>
    /// </summary>
    public CanvasSceneCollection CanvasScenes { get; init; } = new();

    /// <summary>
    /// 打开图片时用窗口还是全屏。<b>当场生效</b>：下一次点「图片」就按它来。
    /// </summary>
    public ImageOpenMode ImageOpenMode { get; init; } = ImageOpenMode.Window;

    /// <summary>
    /// 启动时把上次打开的那张（或那几张）图重新打开。
    /// <para>关掉它就等于"每次都从空白开始" —— 对"我只是临时看一眼"的用法更合适。</para>
    /// </summary>
    public bool ImageRestoreOnStartup { get; init; }

    /// <summary>
    /// 上次打开图片时所在的那个目录，文件选择框从这儿起。
    /// <para>
    /// 存它是因为"每次都从文档目录开始翻"是那种很小但天天遇的烦。
    /// <b>空串</b>表示还没打开过任何图，那就不设初始目录（由系统决定）。
    /// </para>
    /// </summary>
    public string LastImageDirectory { get; init; } = "";

    /// <summary>
    /// 最近打开过的图片文件（新的在前，最多 <see cref="MaxRecentImages"/> 个）。
    /// <para>
    /// 存<b>文件</b>而不只是目录，是因为"启动时打开上次的图片"要的是那几张具体文件 ——
    /// 只记目录的话启动后只能打开文件夹让用户自己再点一遍。
    /// </para>
    /// </summary>
    public RecentImageCollection RecentImages { get; init; } = new();
}

/// <summary>UI-thread-owned preferences. Slider changes are debounced; writes replace atomically.</summary>
internal static class AppPreferences
{
    private static string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanStartWrite", "preferences.json");
    private static DispatcherTimer? _saveTimer;
    private static bool _initialized;
    private static bool _pending;
    private static bool _applyingFromPreferences;
    internal static bool IsSavePending => _pending;
    internal static PreferenceSnapshot Current { get; private set; } = new();
    internal static string? SaveError { get; private set; }
    internal static event Action<PreferenceSnapshot>? Changed;
    internal static event Action? SaveStatusChanged;

    internal static void Initialize(string? storagePath = null)
    {
        if (_initialized) return;
        _initialized = true;
        if (storagePath is not null) FilePath = Path.GetFullPath(storagePath);
        try
        {
            if (File.Exists(FilePath))
                Current = Validate(JsonSerializer.Deserialize<PreferenceSnapshot>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SaveError = "无法读取偏好设置，本次使用默认值。";
            Trace.WriteLine(ex);
        }
        Current = Validate(Current);
        ApplyInkOptions(Current);
        ApplyTipOptions(Current);
        // 三条桥都是"运行时状态 → 存档"的方向：状态在运行时被改，这里把它记下来。
        // 反方向（存档 → 运行时）只在 Initialize 与 Update 里走，且由 _applyingFromPreferences 挡住回环。
        InkRuntimeOptions.Changed += options =>
        {
            if (_applyingFromPreferences) return;
            Update(Current with { Pressure = options.EnablePressure });
        };
        // 笔锋的<b>取值</b>不用在这里桥：它属于某一支笔，那支笔动了就是工具列表动了（见下一条）。
        // 这里只桥"我的笔锋"那份预设库 —— 它是全应用共享的。
        InkTipOptions.PresetsChanged += CaptureCustomPresets;
        ToolbarTools.LayoutChanged += CaptureToolbar;
        ToolbarTools.SelectionChanged += CaptureToolbar;
        CanvasOptions.Changed += CaptureCanvasOptions;
    }

    internal static void Update(PreferenceSnapshot value)
    {
        value = Validate(value);
        if (value == Current) return;
        Current = value;
        _applyingFromPreferences = true;
        try
        {
            ApplyInkOptions(value);
            ApplyTipOptions(value);
        }
        finally { _applyingFromPreferences = false; }
        _pending = true;
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(value);
        SaveStatusChanged?.Invoke();
    }

    private static DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => Flush();
        return timer;
    }

    internal static void Flush()
    {
        _saveTimer?.Stop();
        if (!_pending) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
            SaveError = null;
            _pending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = "偏好设置未能保存；本次会话内仍然生效。";
            Trace.WriteLine(ex);
        }
        SaveStatusChanged?.Invoke();
    }

    private static PreferenceSnapshot Validate(PreferenceSnapshot value) => value with
    {
        Theme = Enum.IsDefined(value.Theme) ? value.Theme : AppTheme.Light,
        ImageOpenMode = Enum.IsDefined(value.ImageOpenMode) ? value.ImageOpenMode : ImageOpenMode.Window,
        LastImageDirectory = value.LastImageDirectory ?? string.Empty,
        RecentImages = new RecentImageCollection { Items = ValidateRecentImages(value.RecentImages.Items) },
        ToolbarItems = ValidateTools(value.ToolbarItems),
        ToolbarSelectedId = ValidateSelectedTool(value.ToolbarItems, value.ToolbarSelectedId),
        TipCustomPresets = ValidateCustomPresets(value.TipCustomPresets),
        CanvasScenes = ValidateCanvasScenes(value.CanvasScenes),
    };

    /// <summary>
    /// 洗一遍场景设置：丢掉不认识的场景（枚举成员被删过的旧档）、同一场景只留第一个，
    /// 并把底色归到某一档上。
    /// <para>漏掉的场景不补 —— 读取那一侧（<see cref="CanvasOptions.For"/>）查不到就按默认值走，
    /// 而"没有这一项"与"开关全关、底色白纸"本来就是同一件事。</para>
    /// <para>底色要洗：<c>uint</c> 是从文件里来的任意值，一个带透明或落在档外的值
    /// 会让白板这块"盖住桌面的底"变成屏幕上一个洞，而且不报错。</para>
    /// </summary>
    private static CanvasSceneCollection ValidateCanvasScenes(CanvasSceneCollection collection)
    {
        var source = collection.Items ?? [];
        var kept = new List<CanvasSceneSettings>(source.Count);
        var seen = new HashSet<CanvasScene>();

        foreach (var settings in source)
        {
            if (settings is null) continue;
            if (!Enum.IsDefined(settings.Scene)) continue;
            if (!seen.Add(settings.Scene)) continue;
            kept.Add(settings with
            {
                BackgroundArgb = CanvasBackgroundPalette.Normalize(settings.BackgroundArgb),
            });
        }

        return new CanvasSceneCollection { Items = kept };
    }

    /// <summary>
    /// 当前选中的工具标识：列表里查不到就落空串，由 <see cref="ToolbarTools.Load"/> 自己挑一个。
    /// <para>
    /// 这里查的是<b>洗过的那份列表</b>而不是原始入参 —— 选中的那支笔可能刚好被上面一步删掉了
    /// （重复标识、超上限、固定项重复），查原始列表会留下一个指向不存在项的选中态。
    /// </para>
    /// </summary>
    /// <summary>
    /// 洗一遍"最近打开的图片"：去重、去空、砍到上限。
    /// <para>
    /// <b>不检查文件还在不在</b>：外接盘/U 盘拔掉之后那些路径会失效，但把它们删掉等于
    /// "下次插回去就没有这一项了"，而用户对"最近用过"的预期是它记得，文件没了再说。
    /// 真正打开时打不开，那一条会在打开时单独跳过（有明确的失败面），不牵连别的条目。
    /// </para>
    /// </summary>
    private static List<string> ValidateRecentImages(List<string>? recent)
    {
        if (recent is not { Count: > 0 }) return [];

        var kept = new List<string>(Math.Min(recent.Count, MaxRecentImages));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in recent)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!seen.Add(path)) continue;
            kept.Add(path);
            if (kept.Count >= MaxRecentImages) break;
        }

        return kept;
    }

    private static string ValidateSelectedTool(ToolbarToolCollection source, string selectedId)    {
        if (string.IsNullOrEmpty(selectedId)) return string.Empty;
        var items = source.Items ?? [];
        foreach (var tool in items)
        {
            if (string.Equals(tool.Id, selectedId, StringComparison.Ordinal)) return selectedId;
        }

        return string.Empty;
    }

    /// <summary>
    /// 洗一遍工具栏项。四件事，每一件都对应一种"坏存档"：
    /// <list type="number">
    /// <item>丢坏项：标识为空 / 类型不认识 / 标识重复；</item>
    /// <item>固定项去重：鼠标模式、撤销、重做、设置各只留第一个；</item>
    /// <item>固定项补齐：缺了就补回一个 —— <b>少了鼠标模式用户出不去这块画布，少了白板就再也进不去那块，
    /// 少了设置就再也改不了工具栏</b>，所以这几个不是"用户数据"，是这套界面的门槛；
    /// 补的时候落在它该在的那一格（见下面那段注释）；</item>
    /// <item>数据规范化：区间钳制、非有限值退回默认、与类型无关的字段一律清回默认值
    /// （免得它们变成第二个真相）。</item>
    /// </list>
    /// </summary>
    private static ToolbarToolCollection ValidateTools(ToolbarToolCollection collection)
    {
        // 列表整个是空的 = 第一次运行（或存档被清空）：给回默认那一条工具栏。
        // 少了这一步，首启会得到一条"只有鼠标 / 撤销 / 重做 / 设置、一支笔都没有"的工具栏 ——
        // 因为下面补的是四个门槛项，而笔与橡皮是用户数据，不会凭空长出来。
        var source = collection.Items is { Count: > 0 } items ? items : ToolbarTools.DefaultItems();

        var kept = new List<ToolbarTool>(source.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fixedSeen = new HashSet<ToolbarToolKind>();

        foreach (var tool in source)
        {
            if (kept.Count >= ToolbarTools.MaxItems) break;
            if (tool is null) continue;
            if (string.IsNullOrWhiteSpace(tool.Id)) continue;
            if (!Enum.IsDefined(tool.Kind)) continue;
            if (!ids.Add(tool.Id)) continue;

            if (IsFixedKind(tool.Kind))
            {
                if (!fixedSeen.Add(tool.Kind)) continue;
            }

            kept.Add(ToolbarTools.Normalize(tool));
        }

        foreach (var kind in FixedKinds)
        {
            if (fixedSeen.Contains(kind)) continue;
            if (kept.Count >= ToolbarTools.MaxItems) break;

            var fallback = ToolbarTools.DefaultItems().Find(item => item.Kind == kind)!;
            if (!ids.Add(fallback.Id)) continue; // 标识被别人占了：宁可不补，也不造一个重复标识

            // 补齐落在它该在的那一格，而不是一律追加到尾巴：白板紧跟鼠标、图片紧跟白板 ——
            // 后者依赖前者已经被补进去（FixedKinds 的顺序就是 Mouse → Whiteboard → Image），
            // 所以这里查"前一颗"一定查得到。追加到尾巴的话，同一份设置在"首启"与"升级后"长得不一样，
            // 而这种差别只会以"我的按钮顺序怎么变了"的形式被用户看见。
            var at = kind switch
            {
                ToolbarToolKind.Whiteboard => IndexAfterKind(kept, ToolbarToolKind.Mouse),
                ToolbarToolKind.Image => IndexAfterKind(kept, ToolbarToolKind.Whiteboard),
                _ => kept.Count,
            };
            kept.Insert(at, fallback);
        }

        return new ToolbarToolCollection { Items = kept };
    }

    /// <summary>某一类项在那一格之后；这一类不在列表里时返回末尾。</summary>
    private static int IndexAfterKind(List<ToolbarTool> items, ToolbarToolKind kind)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i].Kind == kind) return i + 1;

        return items.Count;
    }

    /// <summary>
    /// 六个"界面门槛"项：鼠标模式（退出这块画布）、白板与图片（进那两块画布的唯一入口）、
    /// 撤销、重做、设置（唯一能改工具栏的入口）。它们各只允许有一个，而且不许缺失。
    /// </summary>
    private static readonly ToolbarToolKind[] FixedKinds =
    [
        ToolbarToolKind.Mouse, ToolbarToolKind.Whiteboard, ToolbarToolKind.Image,
        ToolbarToolKind.Undo, ToolbarToolKind.Redo, ToolbarToolKind.Settings,
    ];

    private static bool IsFixedKind(ToolbarToolKind kind)
    {
        foreach (var fixedKind in FixedKinds)
        {
            if (fixedKind == kind) return true;
        }

        return false;
    }


    /// <summary>
    /// 过滤自定义预设：标识为空的、以及和内置档位撞标识的一律丢掉（撞内置的那条会让
    /// "内置不可覆盖"这条规则被绕过），同标识的去重（后写的胜），再按引擎的预设作用域
    /// 把取值对齐并钳制。数量与名字长度都有上限 —— 这个文件是要被反复读写的，不能无界增长。
    /// </summary>
    private static TipPresetCollection ValidateCustomPresets(TipPresetCollection presets)
    {
        if (presets.Items is not { } stored) return new TipPresetCollection();

        var kept = new List<TipPresetRecord>();
        var parameters = StrokeTipParameters.PresetScoped;

        foreach (var preset in stored)
        {
            if (kept.Count >= MaxCustomPresets) break;
            if (preset is null) continue;
            if (string.IsNullOrWhiteSpace(preset.Id)) continue;
            if (!IsCustomPresetId(preset.Id)) continue;
            if (kept.Any(existing => existing.Id == preset.Id)) continue;

            var values = new double[parameters.Count];
            var source = preset.Values ?? [];
            for (var i = 0; i < parameters.Count; i++)
            {
                var raw = i < source.Length ? source[i] : parameters[i].DefaultValue;
                values[i] = double.IsFinite(raw)
                    ? Math.Clamp(raw, parameters[i].Minimum, parameters[i].Maximum)
                    : parameters[i].DefaultValue;
            }

            var name = (preset.Name ?? string.Empty).Trim();
            if (name.Length == 0) name = "我的笔锋";
            if (name.Length > MaxPresetNameLength) name = name[..MaxPresetNameLength];

            kept.Add(new TipPresetRecord { Id = preset.Id, Name = name, Values = values });
        }

        return new TipPresetCollection { Items = kept };
    }

    /// <summary>
    /// 自定义预设的标识必须以 <see cref="CustomPresetIdPrefix"/> 开头。
    /// 内置档位的标识（<c>none</c> / <c>standard</c> / …）因此不可能与自定义撞上，
    /// 而"内置不可覆盖"这条引擎规则也就不会被存档绕过。
    /// </summary>
    internal const string CustomPresetIdPrefix = "custom.";

    internal static bool IsCustomPresetId(string id) =>
        id.Length > CustomPresetIdPrefix.Length && id.StartsWith(CustomPresetIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 自定义预设的上限。<b>它是唯一的那个数</b>：存档这一侧用它截断，应用这一侧用它
    /// 决定还能不能存 —— 两处不一致的后果是"存进去了但下一次启动它没了"。
    /// </summary>
    internal const int MaxCustomPresets = 32;

    /// <summary>"最近打开的图片"最多留几条。够一个人来回翻，又不至于把存档撑成一份日志。</summary>
    internal const int MaxRecentImages = 10;

    private const int MaxPresetNameLength = 24;

    /// <summary>把「我的笔锋」记进存档。空写入被 <see cref="Update"/> 的相等判断挡掉。</summary>
    private static void CaptureCustomPresets()
    {
        if (_applyingFromPreferences) return;
        Update(Current with
        {
            TipCustomPresets = new TipPresetCollection { Items = [.. InkTipOptions.CustomPresetRecords] },
        });
    }

    /// <summary>
    /// 把工具栏记进存档：<b>有哪些按钮、什么顺序、各自什么数据、选中哪一个</b>。
    /// 画笔粗细、橡皮擦法、笔锋取值都在这条路上 —— 它们现在属于某一项，不再有各自的桥。
    /// </summary>
    private static void CaptureToolbar()
    {
        if (_applyingFromPreferences) return;
        Update(Current with
        {
            ToolbarItems = new ToolbarToolCollection { Items = [.. ToolbarTools.Snapshot()] },
            ToolbarSelectedId = ToolbarTools.SelectedId,
        });
    }

    /// <summary>把画布的设置记进存档。<b>按场景整份写回</b> —— 快照本来就是全场景的，
    /// 只写变了的那个场景会让别的场景的设置在下一次存盘时被抹掉。
    /// 参数因此只用来在日志里说清是谁变了，不参与挑选要写什么。</summary>
    private static void CaptureCanvasOptions(CanvasScene changedScene)
    {
        if (_applyingFromPreferences) return;
        Update(Current with { CanvasScenes = CanvasOptions.Snapshot() });
    }

    private static void ApplyInkOptions(PreferenceSnapshot value)
    {
        InkRuntimeOptions.SetEnablePressure(value.Pressure);
    }

    /// <summary>
    /// 把存档翻译成运行时状态。三条，顺序有讲究：
    /// <b>先装「我的笔锋」这份预设库，再装工具栏</b> —— 某一支笔引用的档位标识要能在库里查到，
    /// 否则它会在装载时被当成坏标识降级成"自定义"，用户会看到自己挂的档位莫名其妙没了。
    /// 画布设置与这两者无依赖，放最后。
    /// </summary>
    private static void ApplyTipOptions(PreferenceSnapshot value)
    {
        InkTipOptions.LoadCustomPresets(value.TipCustomPresets);
        ToolbarTools.Load(value.ToolbarItems.Items ?? [], value.ToolbarSelectedId);
        CanvasOptions.Load(value.CanvasScenes);
    }
}
