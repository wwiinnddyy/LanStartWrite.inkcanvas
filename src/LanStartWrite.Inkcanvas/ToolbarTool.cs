using System.Text.Json.Serialization;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>颜色 ↔ ARGB 整数。<b>存档里只放整数</b>，不放 <see cref="Color"/>：
/// 后者是框架类型，序列化格式由框架决定，而这份格式是要长期读写的。</summary>
internal static class Argb
{
    internal static uint Pack(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    internal static Color Unpack(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

/// <summary>
/// 工具栏上的<b>一项</b>：按钮的身份 + 它自己那套数据。
/// <para>
/// <b>为什么所有类型的字段挤在一条记录里</b>：这是一份要落盘的扁平数据，
/// 按 <see cref="Kind"/> 决定哪几个字段有意义（笔读颜色 / 粗细 / 笔型 / 笔锋，橡皮读擦法 / 半径，
/// 分隔线与三个系统钮一个都不读）。做成继承层次或多张表，代价是存档多一层判别式，
/// 而收益只是"字段看起来更整齐" —— 不值得。无意义的字段在写入时被规范化成默认值，
/// 因此它们的取值不会成为第二个真相。
/// </para>
/// <para>
/// <b>相等性</b>：这条 record 的所有成员都是值类型或按值比较的类型
/// （<see cref="TipValueVector"/> 是显式按值比的），所以合成的 <c>Equals</c> 可以直接用 ——
/// "存盘再读回来相等"那条断言因此能覆盖它。
/// </para>
/// </summary>
internal sealed record ToolbarTool
{
    /// <summary>稳定标识，进存档与选中态。<b>不要重命名</b>。</summary>
    public string Id { get; init; } = "";

    public ToolbarToolKind Kind { get; init; }

    /// <summary>用户可见名，也进工具提示。为空时按 <see cref="Kind"/> 取一个默认名。</summary>
    public string Name { get; init; } = "";

    // ---------------------------------------------------------------- 笔

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint ColorArgb { get; init; } = DefaultColorArgb;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ScreenAnnotationColorArgb { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? WhiteboardColorArgb { get; init; }

    internal uint ColorFor(CanvasScene scene) => scene == CanvasScene.Whiteboard
        ? WhiteboardColorArgb ?? ColorArgb
        : ScreenAnnotationColorArgb ?? ColorArgb;

    /// <summary>笔宽（设备无关单位）。</summary>
    public double Thickness { get; init; } = 4;

    public PenKind PenKind { get; init; } = PenKind.Pen;

    /// <summary>
    /// 笔锋档位的稳定标识；空串表示"这支笔的形状是手调的、不对应任何一档"。
    /// 与 <see cref="TipValues"/> 一起决定选中这支笔时把画布调成什么样。
    /// </summary>
    public string TipPresetId { get; init; } = "standard";

    /// <summary>
    /// 这支笔的笔锋取值（全量 18 项）。
    /// <para>
    /// <c>null</c> 表示"跟着 <see cref="TipPresetId"/> 那一档走"—— 新加的笔就是这样，
    /// 它的形状由档位决定，不预存一份拷贝。用户一旦调过参数，这里就变成显式的一份，
    /// 于是两支笔各自的微调互不影响（这正是"两个笔按钮数据独立"的落点）。
    /// </para>
    /// </summary>
    public TipValueVector? TipValues { get; init; }

    /// <summary>这支笔的笔锋总开关。</summary>
    public bool TipEnabled { get; init; } = true;

    // ---------------------------------------------------------------- 橡皮

    public EraserMode EraseMode { get; init; } = EraserMode.Area;

    public double EraserRadius { get; init; } = 14;

    internal const uint DefaultColorArgb = 0xFF202020;

    /// <summary>
    /// 这一项是不是"能被用户删掉 / 复制"的。三个系统钮与鼠标模式<b>不在其列</b>：
    /// 鼠标模式是退出批注的唯一入口，设置是改工具栏的唯一入口，撤销重做是基本盘 ——
    /// 让它们能被删掉，用户就有办法把自己关在门外。
    /// </summary>
    internal bool IsEditable =>
        Kind is ToolbarToolKind.Pen or ToolbarToolKind.Eraser or ToolbarToolKind.Separator;

    /// <summary>给日志与断言看的一行：<c>名字(标识)</c>。</summary>
    public override string ToString() => $"{Name}({Id})";
}

/// <summary>
/// 工具栏的项集合，按显示顺序。
/// <para>
/// 包一层是为了<b>按值比较</b> —— 理由与 <see cref="TipPresetCollection"/> 一字不差：
/// record 的合成 <c>Equals</c> 对 <see cref="List{T}"/> 是按引用比的，
/// 直接放进去会让"存盘再读回来相等"那条断言在往返之后变红。
/// </para>
/// </summary>
internal sealed record ToolbarToolCollection
{
    /// <summary>可空理由同 <see cref="TipValueVector.Values"/>：它从文件里来。</summary>
    public List<ToolbarTool>? Items { get; init; } = [];

    public bool Equals(ToolbarToolCollection? other)
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
