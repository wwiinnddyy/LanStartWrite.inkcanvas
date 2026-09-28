using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jalium.UI.Interop;

namespace LanStartWrite.Inkcanvas.Diagnostics;

/// <summary>
/// 「现在是哪套渲染」的唯一读法 —— 设置页那一块、日志开头那一行、复制诊断信息那一下，
/// 三处都从这里取，所以它们不会各说各话。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么值得单独一个类</b>：这三处以前根本没有，而它们一旦各写各的就会出现
/// "面板说 D3D12、日志说 Vulkan、用户截图又是第三个" —— 而排查渲染问题的时候，
/// 三个说法里有两个是编的，比一个都没有更难办。
/// </para>
/// <para>
/// <b>读不到适配器时必须明说读不到</b>，不能显示空白：这一段代码存在的意义是
/// "出问题的时候看"，而"出问题"经常就包括图形栈本身不完整。实测就撞到过 ——
/// <c>GetAdapterInfo()</c> 返回 null，于是探针打出 <c>adapter=unavailable</c>。
/// 那一格留白会让这个面板在它最该说话的时候一声不吭。
/// </para>
/// </remarks>
internal static class RenderInfo
{
    /// <summary>一格：标题 + 值。</summary>
    internal readonly record struct Row(string Label, string Value);

    /// <summary>
    /// 当前实际在跑的后端与引擎（不是"用户选了哪个"——那两件事正是要分开的）。
    /// </summary>
    /// <remarks>
    /// <b>按需读，不缓存</b>。原先这两个是 <c>{ get; } = …</c> 的静态初始化字段，于是值在
    /// <b>类型第一次被用到</b>的那一刻定死 —— 而引擎是在上下文建好<b>之后</b>才赋的，
    /// 初始化早了那一步就读到 <see cref="RenderingEngine.Auto"/> 并一直拿着它。
    /// 症状是面板上"在跑的引擎"写着自动、而下面那一行的实时读数写着 Impeller：
    /// <b>同一块面板里两行自相矛盾，而它恰恰是出问题时候唯一的依据。</b>
    /// 探针里那条断言就是这么红的。
    /// </remarks>
    internal static RenderBackend CurrentBackend => RenderContext.Current?.Backend ?? RenderBackend.Auto;

    internal static RenderingEngine CurrentEngine => RenderContext.Current?.DefaultRenderingEngine ?? RenderingEngine.Auto;

    /// <summary>还没有渲染上下文时，"现在跑的是什么"是<b>不知道</b>，不是"自动"。</summary>
    internal static bool HasContext => RenderContext.Current is not null;

    /// <summary>
    /// <b>启动时真正应用下去的后端与引擎</b>，由 <c>Program.Main</c> 记下来。
    /// </summary>
    /// <remarks>
    /// <para>
    /// "要不要提示重启"只跟这个比，<b>不跟 <c>RenderContext</c> 自报的值比</b>。
    /// </para>
    /// <para>
    /// 原因是实测出来的：<c>DefaultRenderingEngine</c> 的读数<b>不是稳定的</b> ——
    /// 同一上下文、同一时刻连读两次一致，可渲染完一帧之后它就从 Impeller 变成 Auto。
    /// 拿它当基准的后果是提示条亮不亮全看"刚才有没有渲染过一帧"，
    /// 而它是给用户看的那条提示。同一时刻两次读一致这条本身有断言守着，
    /// 变的是<b>帧与帧之间</b>，所以断言绿、行为飘。
    /// </para>
    /// <para>
    /// 而"上次启动时用的是 X，你选了 Y"既是稳定的事实，也是用户真正问的那句话。
    /// </para>
    /// </remarks>
    internal static RenderBackend? AppliedBackend { get; private set; }

    internal static RenderingEngine? AppliedEngine { get; private set; }

    /// <summary>这次<b>要</b>的后端。<c>tools/RenderProbe</c> 实测它可能和落点不一样。</summary>
    internal static RenderBackend? RequestedBackend { get; private set; }

    /// <summary>由 <c>Program.Main</c> 在应用完偏好之后调一次。</summary>
    /// <remarks>
    /// <b>要的</b>与<b>落到的</b>分开记，因为框架在失败时会静默落回别的后端：
    /// 探针里 <c>Metal</c> 请求在这台 Windows 上落回 <c>Software</c>，<b>而且不抛异常</b>。
    /// 那种情况如果只记落点，就永远是"用的 Software，没问题"，
    /// 而用户明明选了 Metal —— 于是这一格成了唯一能说出"你要的没换成"的地方。
    /// </remarks>
    internal static void RecordApplied(RenderBackend requested, RenderBackend landed, RenderingEngine engine)
    {
        RequestedBackend = requested;
        AppliedBackend = landed;
        AppliedEngine = engine;
    }

    /// <summary>
    /// 「自动」落成实际值 —— <b>与 <c>Program</c> 建上下文用的是同一个函数</b>。
    /// </summary>
    /// <remarks>
    /// 这条一开始是各写一份的，而两份必然会漂：不落成 Impeller 的话，
    /// 偏好里存的是"自动"、跑着的是 Impeller，于是"用户选择"与"实际在跑"永远不等，
    /// 重启提示会一直亮着 —— 而它没有一次是真的。
    /// </remarks>
    internal static RenderingEngine ResolveEngine(RenderingEngine wanted) =>
        wanted == RenderingEngine.Auto ? RenderingEngine.Impeller : wanted;

    /// <summary>
    /// 要不要提示重启。<b>「自动」不需要</b>：它的意思就是"让框架挑"，
    /// 下次启动在同一个系统上还是会挑到同一个，所以只在**显式选了别的**时才提示。
    /// </summary>
    /// <remarks>
    /// 基准是 <see cref="AppliedBackend"/> / <see cref="AppliedEngine"/>（启动时真应用下去的），
    /// <b>不是</b>上下文自报的值 —— 理由见那两个属性的说明。还没记下来（<c>null</c>）时
    /// 一律判"不提示"：那时"上次用的是什么"是<b>不知道</b>，
    /// 拿"不知道"去比一遍会得出"要重启"，于是用户刚启动就看见一条催重启的提示。
    /// </remarks>
    internal static bool NeedsRestart(RenderBackend wanted) =>
        AppliedBackend is not null && wanted != RenderBackend.Auto && wanted != AppliedBackend.Value;

    internal static bool NeedsRestart(RenderingEngine wanted) =>
        AppliedEngine is not null && ResolveEngine(wanted) != AppliedEngine.Value;

    /// <summary>「自动」落到具体值之后的样子 —— 面板上要显示这个，因为<em>跑着的是</em>它。</summary>
    internal static string CurrentBackendText => Describe(CurrentBackend);

    internal static string CurrentEngineText => Describe(CurrentEngine);

    /// <summary>
    /// 全部诊断行。<b>没有一行是猜的</b>：读不到就写"读不到"，不写默认值。
    /// </summary>
    internal static IReadOnlyList<Row> Rows()
    {
        var context = RenderContext.Current;
        if (context is null)
        {
            return [new Row("渲染状态", "还没有渲染上下文（应用刚启动，或上下文已失效）")];
        }

        var rows = new List<Row>
        {
            new("渲染后端", Describe(context.Backend)),
            new("渲染引擎", Describe(context.DefaultRenderingEngine)),
            new("上下文", $"有效={YesNo(context.IsValid)} 代次={context.Generation} 句柄=0x{context.Handle:X}"),
        };

        // 适配器这一格单独 try：它读的是驱动给的底层信息，出错的概率比别的格高，
        // 而它一错就**整块面板都没有**——所以不许它把别的行带走。
        string adapterText;
        try
        {
            var adapter = context.GetAdapterInfo();
            // 成员一律用 `?.` 取，不先判空再取 —— `AdapterInfo?` 上不带 `?.` 的成员访问
            // 报的是 CS1061「未包含该成员的定义」，看着像"类型缺字段"，而真实原因是可空性。
            var name = adapter?.Name;
            var type = adapter?.AdapterType.ToString();
            adapterText = adapter is null || name is null
                ? "读不到（驱动没有报告适配器信息——这本身可能就是问题所在）"
                : $"{name}（{type}）";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or NotSupportedException or InvalidOperationException)
        {
            adapterText = $"读取时出错：{ex.GetType().Name} {ex.Message}";
        }
        rows.Add(new Row("图形适配器", adapterText));

        // 要的那一档与实际落点不一致时必须说出来。框架失败会**静默落回**另一档而不抛异常，
        // 于是"用户选了 X、界面没报任何事、其实跑的是 Y"—— 只看落点永远发现不了。
        if (RequestedBackend is { } requested && AppliedBackend is { } landed && requested != landed
            && requested != RenderBackend.Auto)
        {
            rows.Add(new Row("后端落点", $"要的是{Describe(requested)}，实际落回{Describe(landed)}（换了档但没换成）"));
        }

        // 上下两行的"引擎"可能不一样（帧与帧之间会变），所以把"启动时用的是哪个"
        // 单开一行：用户看到两处不一样时，这里就是那句话的答案。
        if (AppliedBackend is not null || AppliedEngine is not null)
        {
            rows.Add(new Row("启动时用的是",
                $"后端={Describe(AppliedBackend ?? context.Backend)} 引擎={Describe(AppliedEngine ?? context.DefaultRenderingEngine)}"));
        }

        try
        {
            rows.Add(new Row("设备状态", context.CheckDeviceStatus() ? "正常" : "不正常"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            rows.Add(new Row("设备状态", $"读取时出错：{ex.GetType().Name} {ex.Message}"));
        }

        return rows;
    }

    /// <summary>面板与日志都用人话，不直接印枚举名 —— 枚举名对用户没有意义。</summary>
    internal static string Describe(RenderBackend backend) => backend switch
    {
        RenderBackend.Auto => "自动",
        RenderBackend.D3D12 => "Direct3D 12",
        RenderBackend.Vulkan => "Vulkan",
        RenderBackend.Metal => "Metal",
        RenderBackend.Software => "软件渲染",
        _ => backend.ToString(),
    };

    internal static string Describe(RenderingEngine engine) => engine switch
    {
        RenderingEngine.Auto => "自动",
        RenderingEngine.Vello => "Vello",
        RenderingEngine.Impeller => "Impeller",
        _ => engine.ToString(),
    };

    /// <summary>
    /// 给用户选的那几项，附上人话。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据是<b>这份构建里有没有那个后端的原生实现</b>（<c>jalium.native.*.dll</c> 就在 exe 旁边），
    /// <b>不是</b>"这个平台大概用哪个"。
    /// </para>
    /// <para>
    /// 第一版写的是按平台滤：Windows 只列 D3D12，理由"列出来让人选一个跑不了的比不列更糟"。
    /// 那是个<b>没测过的假设</b>，而它把一个能用的选项藏掉了 ——
    /// Windows 发布目录里明明带着 <c>jalium.native.vulkan.dll</c>（1592 KB），
    /// <c>tools/RenderProbe</c> 实测 <c>Vulkan</c> 在本机可用、落点真的是 Vulkan。
    /// 用户问的第一句就是"为什么不能切到 Vulkan"。
    /// </para>
    /// <para>
    /// <b>带了 ≠ 一定能用</b>：Vulkan 还要 loader、驱动 ICD、队列与扩展都齐。
    /// 所以"这一项在列表里"只承诺"值得一试"，真的换了以后要回来看落点 ——
    /// <see cref="Rows"/> 里那行「后端落点」就是为这件事准备的。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(RenderBackend Value, string Label)> BackendChoices()
    {
        var choices = new List<(RenderBackend, string)> { (RenderBackend.Auto, "自动（交给框架挑）") };
        if (NativePresent("d3d12")) choices.Add((RenderBackend.D3D12, "Direct3D 12（Windows 默认）"));
        if (NativePresent("vulkan")) choices.Add((RenderBackend.Vulkan, "Vulkan"));
        if (NativePresent("metal")) choices.Add((RenderBackend.Metal, "Metal"));
        choices.Add((RenderBackend.Software, "软件渲染（慢，但没有驱动依赖）"));
        return choices;
    }

    /// <summary>某个后端的原生实现有没有随这份构建发布 —— 框架就是靠这些 DLL 找后端的。</summary>
    private static bool NativePresent(string backend) => File.Exists(
        Path.Combine(AppContext.BaseDirectory, $"jalium.native.{backend}.dll"));

    internal static IReadOnlyList<(RenderingEngine Value, string Label)> EngineChoices() =>
    [
        (RenderingEngine.Auto, "自动（= Impeller，与此前一致）"),
        (RenderingEngine.Impeller, "Impeller"),
        (RenderingEngine.Vello, "Vello"),
    ];

    /// <summary>日志开头那一行。用户报问题时会带日志，那一行就是省掉一轮问话的地方。</summary>
    internal static void LogOnce()
    {
        var text = new StringBuilder("环境 ");
        foreach (var row in Rows())
        {
            text.Append(row.Label).Append('=').Append(row.Value).Append("  ");
        }
        AppLog.Write("渲染", text.ToString().TrimEnd());
    }

    /// <summary>「复制诊断信息」按钮复制的那一段：环境 + 版本 + 日志文件位置。</summary>
    internal static string DiagnosticsText()
    {
        var text = new StringBuilder();
        text.AppendLine("揽星书写 诊断信息");
        text.AppendLine("===================");
        foreach (var row in Rows())
        {
            text.AppendLine($"{row.Label}：{row.Value}");
        }

        var version = typeof(RenderInfo).Assembly.GetName().Version;
        text.AppendLine($"应用版本：{version?.Major}.{version?.Minor}.{version?.Build}");
        text.AppendLine($"运行时：{System.Environment.Version}");
        text.AppendLine($"操作系统：{System.Environment.OSVersion}（{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}）");
        text.AppendLine($"用户选择：后端={Describe(AppPreferences.Current.RenderBackend)} 引擎={Describe(AppPreferences.Current.RenderingEngine)}");
        text.AppendLine($"日志文件：{AppLog.FilePath}");
        return text.ToString();
    }

    /// <summary>
    /// 重启。<b>没有"重载"可调</b>，所以是新进程 + 退出当前进程。
    /// </summary>
    /// <remarks>
    /// 先起新进程再退自己：反过来的话，新进程起来时旧进程还占着偏好文件的写锁，
    /// 而新进程读的就是那份文件。起不来就返回 false，让调用方把话说准 ——
    /// "点了没反应"是最难查的一类症状。
    /// </remarks>
    internal static bool TryRestart(out string? error)
    {
        error = null;
        var path = System.Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            error = "拿不到本进程的路径（它可能是被宿主加载的），无法重启。";
            return false;
        }

        try
        {
            AppPreferences.Flush();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        {
            error = $"没能启动新进程：{ex.Message}";
            return false;
        }

        return true;
    }

    private static string YesNo(bool value) => value ? "是" : "否";
}
