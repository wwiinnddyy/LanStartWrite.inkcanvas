using System.Windows;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using LanStartWrite.Inkcanvas.Camera;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 视频展台（实物展台）：在<b>摄像头那幅活画面</b>上批注。
/// </summary>
/// <remarks>
/// <para>
/// <b>形状与白板完全一致</b>（全屏、无边框、批注栏是独立浮窗），而且底栏那一排
/// <b>复用</b>了 <see cref="PagedCanvasWindow"/> 点名要的那几个命名元素，语义换掉：
/// </para>
/// <list type="bullet">
/// <item>翻页两颗 → <b>折叠</b>。只有一面，"第 2 页"不存在。</item>
/// <item>页码文字 → <b>状态文字</b>。已就绪写什么设备什么分辨率，
/// 打不开写为什么 —— 它是用户唯一会去看的地方。</item>
/// <item>加页键 → <b>「存成页」</b>。它本来就是"把这页交给别人"的语义，
/// 而这里正是要把这一页交给图片画布。</item>
/// </list>
/// <para>
/// 这样复用而不是另起一排控件，是因为 <c>InitializeCanvasHost</c> 点名要那五个元素、
/// 给 null 就抛 —— 与其塞两个没用的按钮进去，不如把有用的东西绑上去。
/// </para>
/// <para>
/// <b>活画面是"每帧换一张 <c>BitmapImage</c>"</b>，这是它与前四块画布最本质的差别：
/// 底下的东西一直在动，而墨迹不动。于是三件事成为硬要求：
/// <b>(1)</b> 墨迹世界必须固定（横向 A4，见 <see cref="DocumentCameraPage"/>）；
/// <b>(2)</b> 每帧换完必须释放上一张（否则 30fps = 每秒 30 次 GPU 堆积）；
/// <b>(3)</b> 冻结与"存成页"都必须<b>先拷一份</b>，不能留引用。
/// </para>
/// </remarks>
internal sealed partial class DocumentCameraWindow : PagedCanvasWindow
{
    /// <summary>登记给层级系统用的名字（会出现在 <c>Describe()</c> 与 UiSmoke 的断言里）。</summary>
    private const string CanvasLayerNameText = "视频展台";

    private readonly CameraView _engine;
    private readonly IDocumentCameraFrames _frames;
    private readonly bool _ownsFrameSource;
    private Image? _live;
    private DocumentCameraPage? _page;
    private bool _frozen;
    private bool _disposed;

    /// <summary>存成页的回调：<b>宿主（批注栏）来接管</b>，本窗口不直接去动图片窗口。</summary>
    /// <param name="image">那一帧的拷贝。</param>
    /// <param name="strokes">要一起搬过去的笔迹（宿主负责读引擎那一份）。</param>
    internal Action<BitmapImage, DocumentCameraPage>? CaptureAsPageRequested { get; set; }

    /// <summary>
    /// <b>仅供验收替换帧源</b>：给了就用它，不用就用真机。置回 <c>null</c> 即恢复。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要这个口：CI runner 与大部分开发机<b>既没有摄像头，也没有
    /// Media Foundation 的色彩转换环节</b>（本机实测就是后者 ——
    /// 设备枚举得到 2 台、<c>IsCaptureSupported=True</c>，而 <c>Open</c> 一律
    /// <c>UnsupportedFormat</c>，根因是框架向 source reader 要 <c>RGB32</c>、
    /// UVC 原生却是 NV12/MJPG，中间的色彩转换器 MFT 没装）。
    /// </para>
    /// <para>
    /// 没有这个口，展台的整条路就只能靠人工点，而"人工点过"留不下任何东西。
    /// 有了它，<b>活画面、冻结、镜像、存成页这四件事都能自动验</b> ——
    /// 验的不是"真摄像头出图"，而是"出图之后本应用做的事对不对"，
    /// 而后者才是这个仓库能自己负责的部分。
    /// 真机那一段如实留在 <c>tools/UiSmoke/README.md</c> 的人工清单里。
    /// </para>
    /// </remarks>
    internal static IDocumentCameraFrames? FrameSourceOverride { get; set; }

    public DocumentCameraWindow()
    {
        // AllowsTransparency 必须在 InitializeComponent 之前：白板那条判断的同一个理由。
        AllowsTransparency = false;
        ShowActivated = false;
        InitializeComponent();

        // 层级：一行 Topmost 都不该有（见 WindowLayerManager 的类注释）。
        WindowLayerManager.Register(this, WindowLayer.Canvas, CanvasLayerNameText);

        // 那台"只当引擎"的 CameraView：给 0×0 塞进根格子的一个角落。
        // 它是 Control 不是服务，而它自己会在 OnRender 里画预览 ——
        // 我们要的预览是"一张带 world→screen 变换、压在墨迹面底下的 Image"，
        // 形态不同，所以让它什么都不画，只当采集引擎与帧源。
        _engine = new CameraView { Width = 0, Height = 0, IsHitTestVisible = false };
        if (Content is Grid root)
        {
            root.Children.Add(_engine);
        }

        _frames = FrameSourceOverride ?? new NativeDocumentCameraFrames(_engine);
        _ownsFrameSource = FrameSourceOverride is null;
        _frames.FrameArrived += OnFrameArrived;

        InitializeCanvasHost(InkHost, PageControlHost, PreviousPageButton, NextPageButton, AddPageButton, PageNumberText);
        InitializeSharedCanvas();

        WireControls();
        Closed += (_, _) => Shutdown();
    }

    protected override string CanvasLayerName => CanvasLayerNameText;

    protected override CanvasPage CreatePageCore() =>
        _page = new DocumentCameraPage(new CanvasSurface(Dispatcher, assertLoadedSize: false));

    /// <summary>
    /// 这一块<b>没有"加页"</b>：加页键被改成了「存成页」（它在 <see cref="WireControls"/> 里接走）。
    /// </summary>
    protected override void OnAddPageRequested()
    {
    }

    /// <summary>活画面与状态都靠 code-behind 铺，标记里不声明（与图片画布那张图同一个理由）。</summary>
    protected override void ApplyPageBackdrops()
    {
        ReassertLiveLayerAtBottom();
        ApplyLiveToHost();
        UpdateLiveTransform();
    }

    /// <summary>
    /// 建那张"每帧换一张"的图，并把它压到 <b>InkHost 的索引 0</b>。
    /// </summary>
    /// <remarks>
    /// <b>索引 0 是硬要求</b>：墨迹面之下。压错了页会被墨迹面盖住，
    /// 而"看不见画面"很容易被当成"摄像头没开"——
    /// 更糟的是它<b>不报错</b>。而这个位置会被 <c>ActivatePage</c> 推走
    /// （它要把上一面的 surface 摘掉再挂新的），所以每次换面都要重新断言，
    /// 与 <c>ImageViewerWindow.ReassertImageLayer</c> 同一件事。
    /// </remarks>
    protected override void OnSharedCanvasReady()
    {
        base.OnSharedCanvasReady();

        _live = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Hidden,
        };
        ReassertLiveLayerAtBottom();
    }

    private void ReassertLiveLayerAtBottom()
    {
        if (_live is null) return;
        var index = InkHostGrid.Children.IndexOf(_live);
        if (index != 0)
        {
            InkHostGrid.Children.Remove(_live);
            InkHostGrid.Children.Insert(0, _live);
        }
    }

    private void WireControls()
    {
        AddPageButton.Content = "存成页";
        // 基类在 InitializeSharedCanvas 里已经把这一键接到 OnAddPageRequested（我给的是空实现），
        // 所以这里只**再加**一条走"存成页"，不必先解绑 —— 两个都触发，其中一个是空转。
        AddPageButton.Click += (_, _) => OnCaptureAsPage();

        MirrorButton.Content = "镜像";
        MirrorButton.Click += (_, _) => ToggleMirror();

        FreezeButton.Content = "冻结";
        FreezeButton.Click += (_, _) => ToggleFreeze();

        DeviceCombo.SelectionChanged += (_, _) => OnDeviceChanged();
        FormatCombo.SelectionChanged += (_, _) => OnFormatChanged();

        RefreshDeviceList();
        RefreshMirrorButton();
        RefreshFreezeButton();

        // 开不开摄像头要问"能不能"，而不是先开了再报失败 ——
        // 用户看到一块空白然后才知道坏了，比看不到任何提示更难受。
        StartIfPossible();
    }

    // ────────────────────────────────────────────── 设备与分辨率

    private void RefreshDeviceList()
    {
        var devices = _frames.Devices;
        // 用 Array.Empty 而不是集合表达式 []：ItemsSource 是 IEnumerable，
        // 空集合表达式推不出具体类型（CS9174），而这里的重点是"空列表是合法状态"。
        DeviceCombo.ItemsSource = devices.Count == 0
            ? Array.Empty<string>()
            : devices.Select(d => d.FriendlyName).ToArray();

        if (devices.Count > 0)
        {
            DeviceCombo.SelectedIndex = 0;
        }

        RefreshFormatList();
    }

    private void RefreshFormatList()
    {
        if (CurrentDevice is not { } device)
        {
            FormatCombo.ItemsSource = Array.Empty<string>();
            return;
        }

        // 分辨率下拉的**全部内容来自设备自报的那份**。
        // 自己编一份常见分辨率表就等于和设备对不上，而 MF 遇到对不上的请求会静默降级 ——
        // 症状是"我选了 720p，拿到的还是 480p"，且不报错。
        var formats = device.Formats
            .OrderByDescending(f => f.Width * f.Height)
            .ThenByDescending(f => f.Fps)
            .Select(f => $"{f.Width}×{f.Height}")
            .Distinct()
            .ToArray();

        FormatCombo.ItemsSource = formats;
        if (formats.Length > 0) FormatCombo.SelectedIndex = 0;
    }

    private DocumentCameraDevice? CurrentDevice
    {
        get
        {
            var devices = _frames.Devices;
            var index = DeviceCombo.SelectedIndex;
            return index >= 0 && index < devices.Count ? devices[index] : null;
        }
    }

    private (int Width, int Height, double Fps)? CurrentFormat
    {
        get
        {
            if (CurrentDevice is not { } device) return null;

            // 从设备那份原始表里按"宽×高"找回来：显示串是去重后的顺序，
            // 靠下标反查格式会错位（同一分辨率有多个帧率）。
            // 先问"有没有匹配的"，再用 First() 取 —— CameraFormat 是不可空值类，
            // 直接 FirstOrDefault() 会让 ?. 的语义变得含糊。
            var label = FormatCombo.SelectedItem as string;
            if (label is null) return null;

            var parts = label.Split('×');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
                return null;

            var matches = device.Formats.Where(f => f.Width == w && f.Height == h).ToArray();
            if (matches.Length == 0) return null;

            // 同分辨率取最高帧率的那一档：下拉串是去重过的，反查要自己定"用哪一档"。
            var best = matches.OrderByDescending(f => f.Fps).First();
            return (best.Width, best.Height, best.Fps);
        }
    }

    private void OnDeviceChanged() => RefreshFormatList();

    private void OnFormatChanged() => StartIfPossible();

    private void StartIfPossible()
    {
        var device = CurrentDevice;
        var format = CurrentFormat;

        if (device is not { } dev)
        {
            SetStatus(CameraAvailability.NoDevice,
                "没找到摄像头。接上再点一次视频展台就能重扫；如果是笔记本，确认它没被隐私开关挡着。");
            return;
        }

        if (format is null)
        {
            SetStatus(CameraAvailability.Unknown, "这台摄像头没有报告可用的分辨率。");
            return;
        }

        _frozen = false;
        _page?.FrozenFrame?.Dispose();
        if (_page is not null) _page.FrozenFrame = null;
        RefreshFreezeButton();

        _frames.Start(dev.Id, format.Value.Width, format.Value.Height, format.Value.Fps);
        SetStatus(_frames.Availability, _frames.StatusText);
    }

    // ────────────────────────────────────────────── 每帧推进

    private void OnFrameArrived(BitmapImage frame)
    {
        if (_disposed) return;

        // ★ 上一张必须释放。帧源在发这一帧之前已经把上一张 Dispose 了，
        // 所以这里能安全替换的只有"我们挂在 Image 上、但帧源不管的那一份引用" ——
        // 换句话说：**LiveFrame 指向的那张已经在被回收的路上，冻结要拷而不是留**。
        if (_page is null) return;

        if (_frozen)
        {
            // 冻结中：不接这一帧。这一帧随后会被帧源释放，不留引用。
            return;
        }

        _page.LiveFrame?.Dispose();
        _page.LiveFrame = frame;
        ApplyLiveToHost();
    }

    private void ApplyLiveToHost()
    {
        if (_live is null || _page is null) return;

        var frame = _page.FrozenFrame ?? _page.LiveFrame;
        if (frame is null)
        {
            _live.Source = null;
            _live.Visibility = Visibility.Hidden;
            return;
        }

        _live.Source = frame;
        _live.Visibility = Visibility.Visible;
        UpdateLiveTransform();
    }

    /// <summary>
    /// 重建"世界 → 屏幕"的变换。
    /// </summary>
    /// <remarks>
    /// 抄 <c>ImageViewerWindow.BuildWorldTransform</c> 的<b>两点采样</b>：
    /// 用 <c>View.WorldToScreen</c> 分别问 (0,0) / (1,0) / (0,1) 三个点，
    /// 两个轴向量减掉原点就给出缩放与位移。
    /// <para>
    /// <b>为什么不用引擎的视口矩阵</b>：那条矩阵没有可验证的宿主侧读出口，
    /// 而引擎的视口<b>没有旋转</b>，所以三点足够定一个仿射变换。
    /// </para>
    /// <para>
    /// <b>镜像就插在这条链的最前面</b>（绕页面中线翻）：
    /// 它必须作用在<b>画面</b>上而不是墨迹上 —— 翻墨迹的话字也反了，
    /// 而实物展台要翻的只是"看到的画面"（纸上写的字本来就是正的）。
    /// </para>
    /// </remarks>
    private void UpdateLiveTransform()
    {
        if (_live is null || _page is null) return;

        var view = _page.Surface.View;
        var origin = view.WorldToScreen(new Point2D(0, 0));
        var axisX = view.WorldToScreen(new Point2D(1, 0));
        var axisY = view.WorldToScreen(new Point2D(0, 1));

        var scaleX = axisX.X - origin.X;
        var scaleY = axisY.Y - origin.Y;
        var width = DocumentCameraPage.LogicalWidth;
        var height = DocumentCameraPage.LogicalHeight;

        var group = new TransformGroup();
        if (CanvasOptions.CameraMirror)
        {
            // Scale(-1,1) 把整幅沿 x 翻到 [-w,0]，所以补一个 +w 的位移才落回 [0,w]。
            group.Children.Add(new ScaleTransform(-1, 1));
            group.Children.Add(new TranslateTransform(width, 0));
        }

        group.Children.Add(new ScaleTransform(scaleX, scaleY));
        group.Children.Add(new TranslateTransform(origin.X, origin.Y));

        _live.RenderTransform = group;
        _live.Width = width;
        _live.Height = height;
    }

    protected override void OnViewportChangedCore()
    {
        base.OnViewportChangedCore();
        ReassertLiveLayerAtBottom();
        UpdateLiveTransform();
    }

    // ────────────────────────────────────────────── 冻结 / 镜像 / 存成页

    private void ToggleFreeze()
    {
        if (_page is null) return;

        if (_frozen)
        {
            Unfreeze();
            return;
        }

        // 冻结 = 把"此刻这一帧"拷一份留着，然后停止接新帧。
        // **必须拷**：留引用的话下一帧到达时它就被释放了，
        // 症状是"冻结之后过一会儿画面变白"。
        var frozen = _page.Snapshot();
        if (frozen is null)
        {
            SetStatus(_frames.Availability, "还没有画面可以冻结。");
            return;
        }

        _page.FrozenFrame?.Dispose();
        _page.FrozenFrame = frozen;
        _frozen = true;
        ApplyLiveToHost();
        RefreshFreezeButton();
        SetStatus(_frames.Availability, "已冻结。墨迹留着不动，解冻后画面继续走。");
    }

    private void Unfreeze()
    {
        _frozen = false;
        _page?.FrozenFrame?.Dispose();
        if (_page is not null) _page.FrozenFrame = null;
        ApplyLiveToHost();
        RefreshFreezeButton();
        SetStatus(_frames.Availability, _frames.StatusText);
    }

    /// <summary>冻结中吗（验收与"能不能存成页"都问它）。</summary>
    internal bool IsFrozen => _frozen;

    private void ToggleMirror()
    {
        CanvasOptions.SetCameraMirror(!CanvasOptions.CameraMirror);
        RefreshMirrorButton();
        UpdateLiveTransform();
    }

    private void RefreshMirrorButton()
    {
        if (MirrorButton is null) return;
        MirrorButton.Content = CanvasOptions.CameraMirror ? "镜像：开" : "镜像：关";
    }

    private void RefreshFreezeButton()
    {
        if (FreezeButton is null) return;
        FreezeButton.Content = _frozen ? "解冻" : "冻结";
    }

    private void OnCaptureAsPage()
    {
        if (_page is null) return;

        // 没冻结也能存 —— 存的是"此刻正在显示的那一帧"，而冻结与否只影响
        // 接下来还在不在动。强求先冻结会让用户多按一次，而那一次没有换来任何东西。
        var snapshot = _page.Snapshot();
        if (snapshot is null)
        {
            SetStatus(_frames.Availability, "还没有画面可以存。等摄像头出第一帧再试。");
            return;
        }

        CaptureAsPageRequested?.Invoke(snapshot, _page);
    }

    private void SetStatus(CameraAvailability availability, string text)
    {
        if (StatusText is not null) StatusText.Text = text;
        if (PageNumberText is not null) PageNumberText.Text = text;

        // 只有"没画面可看"才挂横幅：已就绪时那块横幅会盖在画面上方，
        // 而用户正在看画面 —— 每一帧都提醒他"一切正常"是噪声。
        var blocked = availability is not (CameraAvailability.Ready or CameraAvailability.Unknown);
        if (StatusBanner is not null) StatusBanner.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;

        _frames.FrameArrived -= OnFrameArrived;
        // 只释放**自己建的那个**。验收注入的那个归调用方（它还要驱动别的断言），
        // 在这里 Dispose 掉会让后面几条断言拿到一个已释放的源 ——
        // 症状是"前面都绿，最后一条炸"，而原因在三屏之外。
        if (_ownsFrameSource) _frames.Dispose();
        _engine.Stop();

        if (_live is not null)
        {
            _live.Source = null;
            InkHostGrid.Children.Remove(_live);
            _live = null;
        }

        DisposeDerivedResources();
    }

    protected override void DisposeDerivedResources()
    {
        if (_page is null) return;

        // 显式释放两张位图。BitmapImage 是 IDisposable + IReclaimableResource，
        // 而一帧高清是十几 MB —— 不释放就是每次开关展台漏一份。
        _page.LiveFrame?.Dispose();
        _page.LiveFrame = null;
        _page.FrozenFrame?.Dispose();
        _page.FrozenFrame = null;
    }

    // ────────────────────────────────────────────── 验收入口
    //
    // 下面这几个是**给 UiSmoke 的窄口**，每一个都对应一条要钉住的断言。
    // 不做成"把整个窗口状态一股脑暴露出去"是因为那样验收会开始断言实现细节
    // （比如"那个 TransformGroup 里有几个子项"），而实现一改就红 —— 那样的红没人会去修。

    /// <summary>活画面那张图在宿主里的下标（-1 = 还没建）。<b>必须是 0</b>，否则它压住墨迹。</summary>
    internal int LiveLayerIndex => _live is null ? -1 : InkHostGrid.Children.IndexOf(_live);

    /// <summary>活画面那张图当前有图吗（冻结住的那张也算）。</summary>
    internal bool HasLiveFrame => _live is { Source: not null, Visibility: Visibility.Visible };

    /// <summary>当前画面显示的是哪一张：活的还是冻的。</summary>
    internal string LiveFrameKind => _page?.FrozenFrame is not null ? "frozen" : "live";

    /// <summary>状态横幅当前显示的字（验收念它，而不是猜窗口有没有挂提示）。</summary>
    internal string BannerText => StatusText?.Text ?? "";

    /// <summary>横幅可见吗（只有"看不了"才该挂出来）。</summary>
    internal bool BannerVisible => StatusBanner?.Visibility == Visibility.Visible;

    /// <summary>活画面那张图的宽高（验收用来对"它是逻辑页而不是摄像头像素"）。</summary>
    internal (double Width, double Height)? HostedLiveSize() =>
        _live is null ? null : (_live.Width, _live.Height);

    /// <summary>
    /// 那张图当前变换的签名。<b>只用于"变了 / 没变"这一个判断</b>，
    /// 所以把各子矩阵的平移与缩放拼一串，不去解释它到底是什么变换。
    /// </summary>
    internal string LiveTransformSignature() =>
        _live?.RenderTransform is TransformGroup group
            ? string.Join("|", group.Children.OfType<Transform>().Select(t =>
                $"{t.GetType().Name}:{t.Value.OffsetX:0.##},{t.Value.OffsetY:0.##},{t.Value.M11:0.###},{t.Value.M22:0.###}"))
            : "(无变换)";

    /// <summary>切冻结（给验收；与点那颗钮走的是同一条路，不是另写一份）。</summary>
    internal void ToggleFreezeForTest() => ToggleFreeze();

    /// <summary>切镜像（同上）。</summary>
    internal void ToggleMirrorForTest() => ToggleMirror();

    /// <summary>存成页（同上）。</summary>
    internal void CaptureAsPageForTest() => OnCaptureAsPage();

    /// <summary>设备下拉里有几项（0 = 一台都没有，这是合法的降级状态）。</summary>
    internal int DeviceCount => DeviceCombo?.Items.Count ?? 0;

    /// <summary>分辨率下拉里有几项。</summary>
    internal int FormatCount => FormatCombo?.Items.Count ?? 0;

    /// <summary>那一页上的笔迹笔数（"存成页"要搬的就是它们）。</summary>
    internal int StrokeCount => _page?.Surface.Document.Strokes.Count ?? 0;
}
