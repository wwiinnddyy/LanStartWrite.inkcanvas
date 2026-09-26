using Jalium.UI;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 缩放的二级菜单：**一条无级滑块 + 一个只读数字框**，0~300%。
/// <para>
/// 与笔 / 橡皮那两个二级菜单同一路子：独立浮窗、按 <see cref="WindowLayer.Panel"/> 登记。
/// 不用 <c>Popup</c> 是因为那三个已经在层级系统里登记过了，而层级是这套的承重墙
/// （见 <c>WindowLayerManager</c>）—— 同一个应用里两种浮层机制，Z 序迟早对不上。
/// </para>
/// <para>
/// 这一层<b>只报意图</b>（"我要 137%"），不自己动视口：缩放是画布的事，
/// 而画布在那 <c>ImageViewerWindow</c> 里。中间那层自己动手的话，
/// 同一个缩放就有了两个能改的地方。
/// </para>
/// <para>
/// <b>滑块的单位是百分数（100 = 原始大小），不是倍数</b>。用户读到的就是"百分之几"，
/// 数字框与滑块同单位，所以两边永远说的是同一件事。
/// </para>
/// </summary>
public partial class ZoomSecondaryMenuWindow : Window
{
    private bool _syncing;

    public ZoomSecondaryMenuWindow()
    {
        InitializeComponent();
        WindowLayerManager.Register(this, WindowLayer.Panel, "缩放");

        ZoomSlider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            RefreshValueBox(ZoomSlider.Value);
            ZoomChanged?.Invoke(ZoomSlider.Value);
        };
    }

    /// <summary>用户拖了滑块。<b>参数是百分数</b>（100 = 原始大小）。</summary>
    internal event Action<double>? ZoomChanged;

    /// <summary>关掉（Esc 或点外面）。</summary>
    internal event Action? DismissRequested;

    /// <summary>探针：滑块当前值（百分数）。</summary>
    internal double SliderPercent => ZoomSlider.Value;

    /// <summary>探针：数字框里写着什么。</summary>
    internal string ValueBoxText => ZoomValueBox.Text;

    /// <summary>那条滑块是不是<b>无级</b>的（不吸到刻度上）。</summary>
    internal bool IsContinuous => !ZoomSlider.IsSnapToTickEnabled;

    /// <summary>范围（百分数）。用户要的是 0~300%。</summary>
    internal (double Min, double Max) PercentRange => (ZoomSlider.Minimum, ZoomSlider.Maximum);

    /// <summary>
    /// 把画布那边的当前倍数搬进来（<b>传的是倍数，内部换算成百分数</b>）。
    /// <b>只搬运，不反过来发事件</b> —— 不压住这一下的话，
    /// 用户在菜单里拖一下滑块就会被自己刚发出去的值改回去（来回抖）。
    /// </summary>
    internal void ShowScale(double scale)
    {
        if (!double.IsFinite(scale)) return;

        _syncing = true;
        try
        {
            ZoomSlider.Value = Math.Clamp(scale * 100, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
        finally
        {
            _syncing = false;
        }

        RefreshValueBox(ZoomSlider.Value);
    }

    private void RefreshValueBox(double percent) => ZoomValueBox.Text = $"{Math.Round(percent):0}%";

    /// <summary>探针：模拟用户把滑块拖到某一档（百分数）。</summary>
    internal void SetSliderFromProbe(double percent) => ZoomSlider.Value = percent;

    /// <summary>Esc 收掉自己。挂在窗口上而不是让宿主判断——浮在最上面那一层自己最清楚。</summary>
    protected override void OnPreviewKeyDown(Jalium.UI.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Jalium.UI.Input.Key.Escape)
        {
            DismissRequested?.Invoke();
            e.Handled = true;
        }
    }
}
