using Dusk.Ink.Input;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 笔锋参数面板：<b>整块界面由引擎的参数描述符表生成</b>，这里没有一项参数是手写的。
/// <para>
/// <b>为什么这么做</b>：引擎的 <see cref="StrokeTipParameters.All"/> 是"参数是什么 /
/// 取值范围 / 默认值 / 说明"的唯一事实源。要是这里按名字抄一遍 18 个滑杆，
/// 引擎哪天加一项参数，界面上就会静默地少一项 —— 那种缺失不会有任何东西报错。
/// 现在反过来：新参数只要登记进引擎那张表，界面下一次打开就自动多出来一行。
/// </para>
/// <para>
/// <b>分组是唯一在应用侧的东西</b>：参数表本身是扁平的（顺序即预设向量的顺序，不能动），
/// 而分组纯粹是"摆在哪一栏"的展示信息。这里按<b>稳定标识</b>归栏 ——
/// 标识按引擎契约不许重命名，所以这张分词表是安全的；万一引擎加了新参数而这张表没跟上，
/// 它会落进末尾的「其它」栏，而不是从界面上消失。
/// </para>
/// <para>
/// 本类<b>只碰引擎的公开入口</b>（描述符的 <c>Get</c> / <c>Set</c>），不自己判断"这个值合不合法" ——
/// 钳制、越界、非有限数全部由描述符那条通道处理。
/// </para>
/// </summary>
internal sealed class StrokeTipEditor
{
    /// <summary>分组定义。顺序即界面从上到下的顺序。</summary>
    private static readonly (string Title, string Description, string[] Ids)[] Sections =
    [
        ("压力来源",
         "宽度曲线从哪来：多大程度听设备压感，多大程度由笔锋自己合成。鼠标这类没有压感的输入，把权重调到 0 才有笔锋。",
         ["devicePressureWeight", "synthesizedPressure", "bodyScale"]),
        ("起笔",
         "落笔那一下的形状。长度一律按弧长算，不按点数 —— 否则采样率高的设备上笔锋会被拉长好几倍。",
         ["entryTaperLength", "entryTaperMinScale", "entryTaperGamma", "entryPressScale", "entryPressPosition"]),
        ("收笔",
         "抬笔那一下的形状。收笔锥形长度同时决定湿墨的临时段窗口，也就是书写时每批重画多少的成本上限。",
         ["exitTaperLength", "exitTaperMinScale", "exitTaperGamma", "exitPressScale", "exitPressPosition"]),
        ("约束",
         "塑形结果的兜底，防止出现零宽几何或接缝落在窗口边界上。",
         ["minPressure", "provisionalMargin"]),
        ("速度",
         "行笔越快笔迹越细。默认关闭 —— 它需要标定过的时间戳，本应用在适配层已按毫秒摊开每一批点。",
         ["velocityInfluence", "velocityReference", "velocityWindowPoints"]),
    ];

    private const string FallbackSectionTitle = "其它";
    private const string FallbackSectionDescription = "引擎参数表里新增、尚未归类的参数。";

    private readonly StrokeTipSettings _settings;
    private readonly List<Row> _rows = [];
    private bool _syncing;

    internal StrokeTipEditor(StrokeTipSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>登记进面板的参数个数。UI 断言与"参数表扩了而界面没跟上"的守卫都看它。</summary>
    internal int ParameterCount => _rows.Count;

    /// <summary>把分组与滑杆建进 <paramref name="host"/>（会清空它）。</summary>
    internal void Build(Panel host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));

        _rows.Clear();
        host.Children.Clear();

        foreach (var section in Grouped())
        {
            var heading = new TextBlock { Text = section.Title };
            heading.SetResourceReference(FrameworkElement.StyleProperty, "SectionTextStyle");
            host.Children.Add(heading);

            var body = new StackPanel(); // 卡片内边距由 SettingsCardStyle（库的 CardBorderStyle + 行距）给
            var description = new TextBlock { Text = section.Description };
            description.SetResourceReference(FrameworkElement.StyleProperty, "HelperTextStyle");
            body.Children.Add(description);

            foreach (var parameter in section.Parameters)
                body.Children.Add(BuildRow(parameter));

            var card = new Border();
            card.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardStyle");
            card.Child = body;
            host.Children.Add(card);
        }

        Sync();
    }

    /// <summary>
    /// 从设置里把所有滑杆的值与数字读回来。<b>参数一变就调它</b>，
    /// 因此"点了预设"和"拖了滑杆"在界面上走的是同一条刷新路径。
    /// </summary>
    internal void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var row in _rows) row.Sync();
        }
        finally { _syncing = false; }
    }

    private FrameworkElement BuildRow(StrokeTipParameter parameter)
    {
        var caption = new TextBlock { Text = parameter.DisplayName };
        caption.SetResourceReference(FrameworkElement.StyleProperty, "BodyTextBlockStyle");

        var value = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(caption);
        Grid.SetColumn(value, 1);
        header.Children.Add(value);

        var step = StepFor(parameter);
        var slider = new Slider
        {
            Minimum = parameter.Minimum,
            Maximum = parameter.Maximum,
            SmallChange = step,
            LargeChange = step * 10,
            Margin = new Thickness(0, 4, 0, 0),
        };
        // 不点样式：FluentJalium 给 Slider 装了隐式样式，代码里建的控件一样命中。
        AutomationProperties.SetName(slider, parameter.DisplayName);

        var content = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        // 工具提示里放"这一项到底在改什么"和取值范围 —— 滑杆本身只能表达一个数。
        content.ToolTip = $"{parameter.Description}\n取值范围 {Format(parameter.Minimum)} – {Format(parameter.Maximum)}";
        content.Children.Add(header);
        content.Children.Add(slider);

        var row = new Row(this, parameter, slider, value, step);
        slider.ValueChanged += (_, _) => OnSliderChanged(row);
        _rows.Add(row);
        return content;
    }

    private void OnSliderChanged(Row row)
    {
        if (_syncing) return;

        var parameter = row.Parameter;

        // 滑杆是连续的，而参数要的是一个"像人挑的"数：先落到步长上，再交给描述符钳制。
        // 不这么做，界面上会出现 1.2380952380952381 这种数字，而且拖动之后
        // 永远回不到预设的取值上 —— 档位归属会莫名其妙地掉成"自定义"。
        var rounded = RoundToStep(row.Slider.Value, parameter.Minimum, row.Step);

        if (Math.Abs(rounded - parameter.Get(_settings)) < 1e-12)
        {
            row.SetText(FormatValue(parameter, rounded));
            return;
        }

        // 写进去就会发通知，设置页据此再走一遍 Sync —— 因此这里不自己刷显示文字。
        parameter.Set(_settings, rounded);
    }

    /// <summary>
    /// 步长：让整条滑杆大约有一百六十格。太密则拖不出整数，太疏则调不出区别。
    /// 这一格同时也是方向键的 <c>SmallChange</c>，所以键盘可用性与鼠标手感是同一个数。
    /// <para>
    /// <b>整数参数要再抬到 1</b>：方向键按固定步长走，而这类参数的 setter 自己会把小数收成整数 ——
    /// 步长 0.2 的话"从 1 按一下右键"会算出 1.2、被收成 1，光标原地不动，键就废了。
    /// </para>
    /// </summary>
    private static double StepFor(StrokeTipParameter parameter)
    {
        var step = NiceStep(parameter.Maximum - parameter.Minimum);
        return step < 1 && IsIntegerValued(parameter) ? 1 : step;
    }

    private static double NiceStep(double span)
    {
        if (!double.IsFinite(span) || span <= 0) return 1;

        var target = span / 160.0;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (var candidate in new[] { 1.0, 2.0, 5.0 })
        {
            if (magnitude * candidate >= target - 1e-12) return magnitude * candidate;
        }

        return magnitude * 10;
    }

    /// <summary>
    /// 探一个参数是不是"整数值"的：往<b>一份临时设置</b>里写个非整数，看它会不会被自己的
    /// setter 收成整数。走临时实例，真实设置与全局状态一行都不碰。
    /// <para>
    /// 用探测而不是在应用侧列一张"哪些是整数"的表：那张表会和引擎的参数表悄悄脱节，
    /// 而脱节的后果（方向键按了不动）不会有任何地方报错。
    /// </para>
    /// <para>
    /// 探针从 <see cref="StrokeTipParameter.Minimum"/> 起跳而不是从默认值起跳：默认值贴着
    /// 上限的那些参数（例如权重档的 1.0，区间 0–1）会算出 1.5、被钳回 1.0，
    /// 于是"写进去的还是个整数"—— 一个假的整数判定，代价是那条滑杆只剩两个位置。
    /// 区间连半个都容不下时按非整数处理：宁可留一个细步长，也不要废掉一条滑杆。
    /// </para>
    /// </summary>
    private static bool IsIntegerValued(StrokeTipParameter parameter)
    {
        var probe = new StrokeTipSettings();
        var fractional = Math.Clamp(parameter.Minimum + 0.5, parameter.Minimum, parameter.Maximum);
        if (Math.Abs(fractional - parameter.Minimum) < 1e-9) return false; // 探不动，别下结论

        parameter.Set(probe, fractional);
        var written = parameter.Get(probe);
        return Math.Abs(written - Math.Round(written)) < 1e-9;
    }

    private static double RoundToStep(double value, double minimum, double step)
    {
        var steps = Math.Round((value - minimum) / step, MidpointRounding.AwayFromZero);
        var snapped = minimum + (steps * step);

        // 浮点乘加会带出 1.1800000000000002 这种尾巴；按步长的小数位收敛一下。
        var decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step)));
        return Math.Round(snapped, Math.Min(decimals, 6));
    }

    /// <summary>
    /// 定位一个参数属于哪一栏。找不到就落到末尾的「其它」栏 ——
    /// 覆盖不全只会让新参数排在该栏，绝不会让它从界面上消失。
    /// </summary>
    private static List<(string Title, string Description, List<StrokeTipParameter> Parameters)> Grouped()
    {
        var groups = new List<(string Title, string Description, List<StrokeTipParameter> Parameters)>(
            Sections.Length + 1);
        foreach (var section in Sections)
            groups.Add((section.Title, section.Description, []));

        var slotById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Sections.Length; i++)
            foreach (var id in Sections[i].Ids) slotById[id] = i;

        var fallback = -1;
        foreach (var parameter in StrokeTipParameters.All)
        {
            if (!slotById.TryGetValue(parameter.Id, out var slot))
            {
                if (fallback < 0)
                {
                    fallback = groups.Count;
                    groups.Add((FallbackSectionTitle, FallbackSectionDescription, []));
                }
                slot = fallback;
            }

            groups[slot].Parameters.Add(parameter);
        }

        return [.. groups.Where(group => group.Parameters.Count > 0)];
    }

    /// <summary>
    /// 数字 + 单位。<b>只有真单位才贴上去</b>：引擎的 <c>Unit</c> 字段里其实混着两种东西 ——
    /// 真单位（压力 / × / 弧长 / 点）与取值轴的说明（<c>0=全合成 / 1=全设备</c>、
    /// <c>&gt;1 快抬 / &lt;1 缓抬</c>）。后者挤在数字旁边会把这一行撑坏，它们只出现在工具提示里。
    /// 判据就取"含不含等号、长不长"：说明一律带 <c>=</c> 且更长。
    /// </summary>
    private static string FormatValue(StrokeTipParameter parameter, double value)
    {
        var text = Format(value);
        var unit = parameter.Unit;
        return unit.Length <= 6 && !unit.Contains('=') ? $"{text} {unit}" : text;
    }

    private static string Format(double value) => value.ToString("0.###");

    private sealed class Row
    {
        private readonly StrokeTipEditor _owner;
        private readonly TextBlock _value;

        internal Row(StrokeTipEditor owner, StrokeTipParameter parameter, Slider slider, TextBlock value, double step)
        {
            _owner = owner;
            Parameter = parameter;
            Slider = slider;
            _value = value;
            Step = step;
        }

        internal StrokeTipParameter Parameter { get; }

        internal Slider Slider { get; }

        /// <summary>本行滑杆的步长。算一次就存着 —— 里面有一步探测，不该在每个拖动事件里重跑。</summary>
        internal double Step { get; }

        internal void Sync()
        {
            var current = Parameter.Get(_owner._settings);
            if (Math.Abs(Slider.Value - current) > 1e-12) Slider.Value = current;
            SetText(FormatValue(Parameter, current));
        }

        internal void SetText(string text) => _value.Text = text;
    }
}
