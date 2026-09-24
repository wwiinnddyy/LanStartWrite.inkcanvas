using Dusk.Ink.Input;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 应用侧的<b>笔锋状态所有者</b>：一支"活的"笔锋设置、当前挂在哪个档位上、
/// 以及用户自己存下来的那几支笔。工具栏、设置页、画布都读它、都只读它。
/// <para>
/// <b>为什么要有这一层</b>：笔锋的注入面只有一处 —— 墨迹控件的 <c>TipSettings</c>。
/// 但"设参数"的地方有三处（笔二级菜单选档位、设置页拖滑杆、重启后读存档），
/// 而控件实例是后建的、还可能被 Dispose 重建。把状态放在控件里，撤销/清空一次都会丢；
/// 放在这里，控件只是一台"把它读进去"的显示器。
/// </para>
/// <para>
/// <b>三层职责</b>：
/// <list type="number">
/// <item>引擎（<see cref="StrokeTipSettings"/> + <see cref="StrokeTipParameters"/>）——
/// 参数是什么、取值范围、怎么塑形，一行都不在这里重写；</item>
/// <item>本类 —— 当前挂哪一档、哪些是"我的笔锋"、什么时候把值推给画布；</item>
/// <item>存档（<see cref="AppPreferences"/>）—— 只负责把它记下来，不参与判断。</item>
/// </list>
/// </para>
/// <para>
/// <b>线程</b>：与偏好设置一致，只在 UI 线程上访问，因此没有锁。
/// </para>
/// </summary>
internal static class InkTipOptions
{
    /// <summary>
    /// 挂载点常量：档位之间比较取值的容差不用浮点等于 —— 反复套用同一档会让
    /// 取值经过几次"读出来再写回去"，逐位相同是常态但不是契约，留一线更稳妥。
    /// </summary>
    private const double ValueEpsilon = 1e-9;

    private static readonly StrokeTipSettings Master = new();

    private static string _presetId = "standard";
    private static int _suppress;

    static InkTipOptions()
    {
        // 引擎在任何参数（含总开关）变化时都会发这个事件，拖滑杆因此能"改一下就看见"。
        // 订阅放在本类里而不是各 UI 里：档位归属的重新认定只有一处该做，做两遍就会打架。
        Master.Changed += OnSettingsChanged;
    }

    /// <summary>笔锋取值（或总开关、或档位归属）发生了变化，画布据此重绘、设置页据此刷新。</summary>
    internal static event Action? Changed;

    /// <summary>预设列表变了（新增 / 删除"我的笔锋"），笔菜单与设置页据此重建下拉项。</summary>
    internal static event Action? PresetsChanged;

    /// <summary>
    /// 应用唯一的笔锋设置实例。<b>它是活的</b>：UI 直接改它就会触发
    /// <see cref="Changed"/>（引擎在参数变化时会发通知），因此不需要一层"设值"接口。
    /// </summary>
    internal static StrokeTipSettings Settings => Master;

    /// <summary>当前笔锋总开关。</summary>
    internal static bool Enabled => Master.Enabled;

    /// <summary>
    /// 当前档位的稳定标识。空串表示<b>自定义</b> —— 取值不来自任何一个预设
    /// （用户拖过滑杆，或删掉了原先选中的那支自定义笔锋）。
    /// </summary>
    internal static string PresetId => _presetId;

    /// <summary>全部预设：内置在前，用户自存的在后。<b>顺序即 UI 顺序</b>。</summary>
    internal static IReadOnlyList<StrokeTipPreset> Presets => StrokeTipPresetLibrary.Default.Presets;

    /// <summary>用户自己保存的预设。</summary>
    internal static IReadOnlyList<StrokeTipPreset> CustomPresets =>
        [.. StrokeTipPresetLibrary.Default.Presets.Where(preset => !preset.IsBuiltIn)];

    /// <summary>当前全部笔锋取值，按 <see cref="StrokeTipParameters.All"/> 的顺序（含速度三项）。</summary>
    internal static double[] CurrentValues => StrokeTipParameters.Capture(Master);

    /// <summary>自定义预设的可序列化形态，交给存档。</summary>
    internal static IReadOnlyList<TipPresetRecord> CustomPresetRecords =>
        [.. CustomPresets.Select(preset => new TipPresetRecord
        {
            Id = preset.Id,
            Name = preset.DisplayName,
            Values = [.. preset.Values],
        })];

    /// <summary>当前是否可以"恢复为所选档位"：没有挂档位（自定义）时无处可恢复，界面上据此灰掉按钮。</summary>
    internal static bool CanReset => _presetId.Length > 0;

    /// <summary>
    /// 还能不能再存一支。<b>上限与存档那一侧共用同一个数</b>：存档会把超出上限的截掉，
    /// 若这里不拦，用户会看到"存进去了、下次启动少了一支"。
    /// </summary>
    internal static bool CanSaveCustomPreset => CustomPresets.Count < AppPreferences.MaxCustomPresets;

    internal static StrokeTipPreset? FindPreset(string? id) =>
        string.IsNullOrEmpty(id) ? null : StrokeTipPresetLibrary.Default.Find(id);

    /// <summary>
    /// 换一支笔锋。传内置档位的标识、或"我的笔锋"的标识都可以 ——
    /// 两者在数据上同构，走的也是同一条应用路径。
    /// <para>
    /// 传空串表示"切到自定义"：<b>只摘掉档位归属，不动任何取值</b>，
    /// 因为用户想表达的正是"别覆盖我调好的参数"。
    /// 传了库里没有的标识则什么都不做（坏 id 是常态，不是异常）。
    /// </para>
    /// </summary>
    internal static void SelectPreset(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            if (_presetId.Length == 0) return;
            _presetId = string.Empty;
            Changed?.Invoke();
            return;
        }

        var preset = StrokeTipPresetLibrary.Default.Find(id);
        if (preset is null) return;

        // 套用期间压住自动认档：这一批写完之后归属由下面那一句显式定，
        // 而不是靠"M 的取值恰好等于某一档"倒推 —— 两个档位取值相同时，倒推会认到先登记的那个。
        _suppress++;
        try { preset.ApplyTo(Master); }
        finally { _suppress--; }

        _presetId = id;
        Changed?.Invoke();
    }

    /// <summary>拨笔锋总开关。关掉之后塑形器原样透传设备压力。</summary>
    internal static void SetEnabled(bool enabled)
    {
        // 引擎的 setter 自己发通知，因此这里不重复推 Changed：
        // 订阅 Master.Changed 的那条路会把档位归属重新认一遍并发出去。
        Master.Enabled = enabled;
    }

    /// <summary>
    /// 把当前所选档位的参数整批写回去，撤掉手动微调。没有挂档位时什么都不做（返回 <c>false</c>）——
    /// "自定义"的意思正是"我不属于任何一档"，此时没有可恢复的目标。
    /// </summary>
    internal static bool ResetToPreset()
    {
        if (_presetId.Length == 0) return false;
        SelectPreset(_presetId);
        return true;
    }

    /// <summary>
    /// 无论当前挂在哪一档，都回到内置的<b>标准</b>档。
    /// 与 <see cref="ResetToPreset"/> 的分工：前者是"把这一档的微调撤掉"，
    /// 本方法是"设置页那个重置按钮要一个确定无疑的落点"。
    /// </summary>
    internal static void ResetToDefault() => SelectPreset("standard");

    /// <summary>
    /// 把此刻调好的参数存成一支"我的笔锋"，并立刻切到它。
    /// <para>
    /// 名字<b>自动生成</b>而不是让用户输入：本应用的 Fluent 主题里没有 TextBox 的样式，
    /// 与其塞一个没经过视觉验收的输入框，不如按"派生自哪一档"起一个一看就懂的名字
    /// （例如「毛笔·改 1」）。重命名是后续的事。
    /// </para>
    /// </summary>
    internal static StrokeTipPreset? SaveCustomPreset()
    {
        var library = StrokeTipPresetLibrary.Default;
        if (!CanSaveCustomPreset) return null;

        var id = NextCustomPresetId(library);
        var name = NextCustomPresetName(library);

        var preset = StrokeTipPreset.Capture(id, name, $"由「{name}」保存的笔锋参数。", Master);
        if (!library.AddOrReplace(preset)) return null;

        _presetId = id;
        PresetsChanged?.Invoke();
        Changed?.Invoke();
        return preset;
    }

    /// <summary>删掉一支"我的笔锋"。内置档位删不掉（引擎会拒绝），删完把归属重新认一遍。</summary>
    internal static bool DeleteCustomPreset(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        var preset = StrokeTipPresetLibrary.Default.Find(id);
        if (preset is null || preset.IsBuiltIn) return false;
        if (!StrokeTipPresetLibrary.Default.Remove(id)) return false;

        if (_presetId == id) _presetId = FindMatchingPresetId();
        PresetsChanged?.Invoke();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 把当前状态推给某个墨迹控件的笔锋设置。<b>这是唯一一条"状态 → 画布"的通道。</b>
    /// <para>
    /// 走引擎的<b>快照</b>往返（按参数名对齐）而不是按下标塞数组：
    /// 名字进格式之后，参数表将来增删，旧档与新档都能对上号；
    /// 而且这一趟是批量写（<c>ApplySnapshot</c> 内部开了批），只触发一次重绘请求。
    /// </para>
    /// </summary>
    internal static void ApplyTo(StrokeTipSettings target)
    {
        if (target is null) return;
        StrokeTipParameters.ApplySnapshot(target, StrokeTipParameters.CaptureSnapshot(Master));
    }

    /// <summary>
    /// 只把「我的笔锋」（用户自存的笔锋预设）装回库里。
    /// <para>
    /// <b>取值不在这里读</b>：一份笔锋参数属于<b>某一支笔</b>（见 <see cref="ToolbarTools"/>），
    /// 由那支笔选中时经 <see cref="LoadState"/> 推过来。这里再读一遍就是第二个真相 ——
    /// 而"两支笔各有一套参数"这件事正是靠"取值只归它那一支笔"成立的。
    /// </para>
    /// </summary>
    internal static void LoadCustomPresets(TipPresetCollection presets)
    {
        var changed = false;
        _suppress++;
        try { changed = SyncCustomPresets(presets.Items ?? []); }
        finally { _suppress--; }

        if (changed) PresetsChanged?.Invoke();
    }

    /// <summary>
    /// 把<b>某一支笔</b>的笔锋状态推成"当前"。这是"选工具"与"参数生效"之间的那一段。
    /// <para>
    /// 两条分支，取决于那支笔有没有自己的一份取值：
    /// <list type="bullet">
    /// <item><paramref name="values"/> 有值 → 用它的取值。<b>手调过的笔走这条，各自独立就落在这里；</b></item>
    /// <item>没有 → 跟着 <paramref name="presetId"/> 那一档走。新加的笔就是这样，不预存一份拷贝 ——
    /// 于是"改了预设的定义，引用它的笔跟着变"这件事还成立。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 档位标识认不得（档位被删了、存档是手写的）时按取值重新认一次，认不出就落到"自定义"，
    /// 而不是硬指一个不存在的名字。既没有取值、档位也不认得时<b>什么都不改</b> ——
    /// "没有这一项"不等于"清成默认"。
    /// </para>
    /// <para>
    /// 只在确有变化时发通知：调用方（选工具）会连着调它，无条件通知会变成
    /// "点一下工具，画布重绘两次"。而且这一条很要紧 —— 通知会触发"把当前参数写回选中的笔"，
    /// 若无条件通知，每选一次笔都会把"跟着档位走"的那支笔物化成一份显式拷贝。
    /// </para>
    /// </summary>
    internal static void LoadState(string presetId, bool enabled, IReadOnlyList<double>? values)
    {
        var valuesChanged = false;
        var presetIdChanged = false;
        var nextPresetId = _presetId;

        _suppress++;
        try
        {
            if (values is { Count: > 0 } explicitValues)
            {
                if (!SameValues(explicitValues))
                {
                    StrokeTipParameters.Apply(Master, explicitValues);
                    valuesChanged = true;
                }

                nextPresetId = ResolvePresetId(presetId);
            }
            else if (StrokeTipPresetLibrary.Default.Find(presetId) is { } preset)
            {
                var before = CurrentValues;
                preset.ApplyTo(Master);
                valuesChanged |= !SameValues(before);
                nextPresetId = presetId;
            }

            if (Master.Enabled != enabled)
            {
                Master.Enabled = enabled;
                valuesChanged = true;
            }

            if (!string.Equals(nextPresetId, _presetId, StringComparison.Ordinal))
            {
                _presetId = nextPresetId;
                presetIdChanged = true;
            }
        }
        finally { _suppress--; }

        if (valuesChanged || presetIdChanged) Changed?.Invoke();
    }

    /// <summary>参数被改动（拖滑杆、套预设、读存档）时重新认档位：取值不再等于任何一档就落到自定义。</summary>
    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_suppress > 0) return;

        _presetId = FindMatchingPresetId();
        Changed?.Invoke();
    }

    /// <summary>把库里的"我的笔锋"调整成存档描述的样子，返回列表是否真的变了。</summary>
    private static bool SyncCustomPresets(IReadOnlyList<TipPresetRecord> records)
    {
        var library = StrokeTipPresetLibrary.Default;
        var changed = false;

        // 先删：存档里已经没有的自定义预设要摘掉（内置的 Remove 会拒绝，所以这里是安全的）。
        foreach (var existing in library.Presets.Where(preset => !preset.IsBuiltIn).ToArray())
        {
            if (records.Any(record => record.Id == existing.Id)) continue;
            changed |= library.Remove(existing.Id);
        }

        // 再补与改名。取值只在新建时写入 —— 改名不该把已经存好的参数重置成默认值。
        foreach (var record in records)
        {
            var existing = library.Find(record.Id);
            if (existing is null)
            {
                // 取值可能是 null（从 JSON 来）：空向量交给引擎按各参数的默认值补齐，
                // 比在应用侧自己编一套默认值更不容易和参数表脱节。
                library.AddOrReplace(new StrokeTipPreset(
                    record.Id, record.Name, $"由「{record.Name}」保存的笔锋参数。", record.Values ?? []));
                changed = true;
            }
            else if (existing.DisplayName != record.Name)
            {
                library.AddOrReplace(new StrokeTipPreset(
                    record.Id, record.Name, existing.Description, [.. existing.Values]));
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 存档里的档位标识还认不认得。认不得（装了旧档、或那支自定义笔锋被删了）就按
    /// 当前取值重新认一次，认不出任何一档就落到自定义 —— 而不是硬指一个不存在的名字。
    /// </summary>
    private static string ResolvePresetId(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        return StrokeTipPresetLibrary.Default.Find(stored) is not null ? stored : FindMatchingPresetId();
    }

    /// <summary>按<b>预设作用域</b>的那几项取值比一遍，返回第一支对得上的预设标识；对不上返回空串。</summary>
    private static string FindMatchingPresetId()
    {
        var current = CapturePresetScoped();
        foreach (var preset in StrokeTipPresetLibrary.Default.Presets)
        {
            if (SamePresetValues(preset.Values, current)) return preset.Id;
        }

        return string.Empty;
    }

    private static double[] CapturePresetScoped()
    {
        var parameters = StrokeTipParameters.PresetScoped;
        var values = new double[parameters.Count];
        for (var i = 0; i < parameters.Count; i++) values[i] = parameters[i].Get(Master);
        return values;
    }

    private static bool SameValues(IReadOnlyList<double> values) =>
        SamePresetValues(CurrentValues, values);

    /// <summary>
    /// 带容差的逐项比较。长度允许不等 —— 存档比参数表短是<b>正常</b>的（旧档），
    /// 缺的那几项按各自的默认值算，而不是一律判"不一样"，否则每次启动都要重写一遍存档。
    /// </summary>
    private static bool SamePresetValues(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        for (var i = 0; i < left.Count; i++)
        {
            var other = i < right.Count
                ? right[i]
                : i < StrokeTipParameters.PresetScoped.Count
                    ? StrokeTipParameters.PresetScoped[i].DefaultValue
                    : StrokeTipParameters.All[i].DefaultValue;

            if (Math.Abs(left[i] - other) > ValueEpsilon) return false;
        }

        return true;
    }

    private static string NextCustomPresetId(StrokeTipPresetLibrary library)
    {
        for (var index = 1; ; index++)
        {
            var candidate = AppPreferences.CustomPresetIdPrefix + index.ToString();
            if (library.Find(candidate) is null) return candidate;
        }
    }

    /// <summary>
    /// 起名：<c>派生档位名·改 N</c>。序列号在同一基名下取名，因此
    /// 「毛笔·改 1 / 毛笔·改 2 / 钢笔·改 1」是一眼能分辨的，而不是一串"我的笔锋 3"。
    /// </summary>
    private static string NextCustomPresetName(StrokeTipPresetLibrary library)
    {
        var baseName = library.Find(_presetId)?.DisplayName;
        if (string.IsNullOrEmpty(baseName)) baseName = "自定义";

        for (var index = 1; ; index++)
        {
            var candidate = $"{baseName}·改 {index}";
            if (library.Presets.All(preset => preset.DisplayName != candidate)) return candidate;
        }
    }
}
