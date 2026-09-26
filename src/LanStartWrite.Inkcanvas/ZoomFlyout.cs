using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 缩放的浮层内容：**一条无级滑块 + 一个只读数字框**，216×44。
/// <para>
/// <b>它不是窗口，是一个控件</b>，由 <see cref="ImageViewerWindow"/> 放进一个
/// <see cref="Popup"/>、锚在那排缩放控件上（见 <c>CreateZoomFlyout</c>）。
/// <para>
/// 之前做成了一个独立的 <c>Window</c>（<c>ZoomSecondaryMenuWindow</c>），于是要自己算位置、
/// 要 <c>WindowLayerManager.Register</c>、要读 <c>Window.IsVisible</c>，并踩了四个坑：
/// 最大化窗口的 <c>Left/Top</c> 是<b>还原位置</b>；<c>PointToScreen</c> 给的是<b>物理像素</b>
/// 而 <c>Left/Top</c> 要 <b>DIP</b>；<c>Show()</c> 之后立刻量 <c>ActualHeight</c> 读到 0；
/// 兜底高度与真实高度不符时菜单就<b>压在锚控件上面</b>。这四个全部是"自己摆位"才有的问题。
/// 左下角那个缩略图浮层用的是 <see cref="Popup"/>，它一直是对的 —— 同一个窗口里的浮层
/// 本来就该是同一种做法。
/// </para>
/// <para>
/// 这一层<b>只报意图</b>（"我要 137%"），不自己动视口：缩放是画布的事。
/// 滑块的单位是<b>百分数</b>（100 = 原始大小），与数字框同单位。
/// </para>
/// </summary>
internal sealed class ZoomFlyout
{
    private const double MinPercent = 0;
    private const double MaxPercent = 300;
    private const double SmallStep = 1;
    private const double LargeStep = 25;

    private readonly Slider _slider;
    private readonly TextBox _valueBox;
    private bool _syncing;

    internal ZoomFlyout()
    {
        // 数字框：跟着滑块走，宽度固定 —— 不固定的话拖滑块时数字会把滑块顶来顶去。
        _valueBox = new TextBox
        {
            Width = 56,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            IsReadOnly = true,
            Text = "100%",
        };

        _slider = new Slider
        {
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
            Minimum = MinPercent,
            Maximum = MaxPercent,
            TickFrequency = 1,
            // 无级是必需的：默认开启时值会吸到 TickFrequency 上，
            // 而"吸上去"把一条连续的手势变成一串台阶 —— 粗调永远差那几 percent。
            IsSnapToTickEnabled = false,
            SmallChange = SmallStep,
            LargeChange = LargeStep,
            Value = 100,
        };
        AutomationProperties.SetName(_slider, "缩放比例");

        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            RefreshValueBox(_slider.Value);
            ZoomChanged?.Invoke(_slider.Value);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_valueBox);
        row.Children.Add(_slider);

        var surface = new Border
        {
            Width = 216,
            Height = 44,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = default,
            Child = row,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "FlyoutSurfaceBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        Root = surface;
    }

    /// <summary>直接塞进 <see cref="Popup.Child"/> 的那一个元素（尺寸写死在上面，不靠内容撑）。</summary>
    internal Border Root { get; }

    /// <summary>用户拖了滑块。<b>参数是百分数</b>（100 = 原始大小）。</summary>
    internal event Action<double>? ZoomChanged;

    /// <summary>探针：滑块当前值（百分数）。</summary>
    internal double SliderPercent => _slider.Value;

    /// <summary>探针：数字框里写着什么。</summary>
    internal string ValueBoxText => _valueBox.Text;

    /// <summary>那条滑块是不是<b>无级</b>的（不吸到刻度上）。</summary>
    internal bool IsContinuous => !_slider.IsSnapToTickEnabled;

    /// <summary>范围（百分数）。用户要的是 0~300%。</summary>
    internal (double Min, double Max) PercentRange => (_slider.Minimum, _slider.Maximum);

    /// <summary>
    /// 把画布那边的当前倍数搬进来（<b>传的是倍数，内部换算成百分数</b>）。
    /// <b>只搬运，不反过来发事件</b> —— 不压住这一下的话，
    /// 用户拖一下滑块就会被自己刚发出去的值改回去（来回抖）。
    /// </summary>
    internal void ShowScale(double scale)
    {
        if (!double.IsFinite(scale)) return;

        _syncing = true;
        try
        {
            _slider.Value = Math.Clamp(scale * 100, _slider.Minimum, _slider.Maximum);
        }
        finally
        {
            _syncing = false;
        }

        RefreshValueBox(_slider.Value);
    }

    /// <summary>探针：模拟用户把滑块拖到某一档（百分数）。</summary>
    internal void SetSliderFromProbe(double percent) => _slider.Value = percent;

    private void RefreshValueBox(double percent) => _valueBox.Text = $"{Math.Round(percent):0}%";
}
