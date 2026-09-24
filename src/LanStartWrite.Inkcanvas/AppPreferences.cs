using System.Diagnostics;
using System.Text.Json;
using Dusk.Ink.Input;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

internal enum AppTheme { Light, Dark, System }

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
    public double PenWidth { get; init; } = 4;
    public bool Pressure { get; init; }

    /// <summary>当前笔锋档位的稳定标识；空串表示"自定义"（取值不来自任何一个预设）。</summary>
    public string TipPresetId { get; init; } = "standard";

    /// <summary>笔锋总开关。关掉之后塑形器原样透传压力。</summary>
    public bool TipEnabled { get; init; } = true;

    /// <summary>
    /// 当前笔锋的<b>全部</b>参数取值（含速度那三项），按 <c>StrokeTipParameters.All</c> 的顺序。
    /// <para>
    /// 存全量而不是只存档位标识：档位只覆盖形状参数，而被手动微调过的速度参数必须能活过重启。
    /// <c>null</c> 表示这份存档里还没有笔锋取值（旧档），此时保持档位默认值。
    /// </para>
    /// </summary>
    public TipValueVector? TipValues { get; init; }

    /// <summary>用户保存的笔锋预设。内置档位<b>不</b>在这里 —— 它们由引擎提供，不需要落盘。</summary>
    public TipPresetCollection TipCustomPresets { get; init; } = new();

    public EraserMode EraseMode { get; init; } = EraserMode.Area;
    public double EraserRadius { get; init; } = 14;
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
        ApplyInkOptions(Current);
        ApplyTipOptions(Current);
        // 两条桥都是"运行时状态 → 存档"的方向：开关在运行时被拨动，这里把它记下来。
        // 反方向（存档 → 运行时）只在 Initialize 与 Update 里走，且由 _applyingFromPreferences 挡住回环。
        InkRuntimeOptions.Changed += options =>
        {
            if (_applyingFromPreferences) return;
            Update(Current with { Pressure = options.EnablePressure });
        };
        InkTipOptions.Changed += CaptureTipOptions;
        InkTipOptions.PresetsChanged += CaptureTipOptions;
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
        EraseMode = Enum.IsDefined(value.EraseMode) ? value.EraseMode : EraserMode.Area,
        PenWidth = double.IsFinite(value.PenWidth) ? Math.Clamp(Math.Round(value.PenWidth), 1, 24) : 4,
        EraserRadius = double.IsFinite(value.EraserRadius) ? Math.Clamp(Math.Round(value.EraserRadius), 4, 48) : 14,
        TipValues = ValidateTipValues(value.TipValues),
        TipCustomPresets = ValidateCustomPresets(value.TipCustomPresets),
    };

    /// <summary>
    /// 把笔锋取值拉回参数表的形状：长度按<b>本表</b>对齐（短了补默认、长了截掉），
    /// 每一项钳到自己的区间，非有限值退回该参数的默认值。
    /// <para>
    /// 这一步同时是"坏存档不许传进引擎"的那道闸：引擎的 <c>Set</c> 通道也钳，
    /// 但它在应用之后才钳，而这里钳完的值会被写回存档 —— 于是坏值在被发现的那一次就被修正掉，
    /// 不会每启动一次重算一遍。
    /// </para>
    /// </summary>
    private static TipValueVector? ValidateTipValues(TipValueVector? vector)
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

    private const int MaxPresetNameLength = 24;

    /// <summary>把运行时的笔锋状态记进存档。空写入被 <see cref="Update"/> 的相等判断挡掉。</summary>
    private static void CaptureTipOptions()
    {
        if (_applyingFromPreferences) return;
        Update(Current with
        {
            TipPresetId = InkTipOptions.PresetId,
            TipEnabled = InkTipOptions.Enabled,
            TipValues = new TipValueVector { Values = InkTipOptions.CurrentValues },
            TipCustomPresets = new TipPresetCollection { Items = [.. InkTipOptions.CustomPresetRecords] },
        });
    }

    private static void ApplyInkOptions(PreferenceSnapshot value)
    {
        InkRuntimeOptions.SetEnablePressure(value.Pressure);
    }

    private static void ApplyTipOptions(PreferenceSnapshot value)
    {
        InkTipOptions.Load(value);
    }
}
