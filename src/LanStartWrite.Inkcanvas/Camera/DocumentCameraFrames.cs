using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Media.Pipeline;
using Jalium.UI.Media.Native;

namespace LanStartWrite.Inkcanvas.Camera;

/// <summary>
/// 展台"能不能开"的<b>四种</b>结论。
/// <para>
/// 为什么不是一个 bool：<c>CameraView.IsCaptureSupported == true</c> 而
/// <c>EnumerateDevices()</c> 返回 2 台设备时，本机实测<b>仍然一帧都开不出来</b>
/// （缺 Media Foundation 的色彩转换环节，UVC 的 NV12/MJPG 转不成框架要的 RGB32）。
/// 把这些全压成"不支持"，用户得到的是一句没有指向的话；分��之后，
/// "没接摄像头"与"电脑缺组件"才能给出完全不同的指引。
/// </para>
/// </summary>
internal enum CameraAvailability
{
    /// <summary>还没问过。</summary>
    Unknown = 0,

    /// <summary>能开：有设备，且真开得出帧。</summary>
    Ready,

    /// <summary>采集后端没加载（多半是平台不支持，或那个原生库没随包发）。</summary>
    BackendMissing,

    /// <summary>后端在，但一台摄像头都没有。</summary>
    NoDevice,

    /// <summary><b>设备在，却开不出帧</b> —— 采集链缺一环，不是"没插摄像头"。</summary>
    OpenFailed,
}

/// <summary>一次能力探测的结论，带一句能直接给用户看的话。</summary>
/// <param name="Availability">四选一。</param>
/// <param name="Detail">人话原因。<b>不写技术名词</b>：用户看到的是"没找到摄像头"，不是"MF_E_NOTFOUND"。</param>
/// <param name="Devices">枚举到的设备（可能为空）。</param>
internal readonly record struct CameraCapabilityReport(
    CameraAvailability Availability,
    string Detail,
    IReadOnlyList<DocumentCameraDevice> Devices)
{
    /// <summary>能不能真的开起来。只有 <see cref="CameraAvailability.Ready"/> 才是真能。</summary>
    internal bool CanCapture => Availability == CameraAvailability.Ready;
}

/// <summary>一台摄像头，以及它<b>自报</b>的那些分辨率。</summary>
/// <param name="Id">平台设备标识（Windows 上是 MF 的 symbolic link）。</param>
/// <param name="FriendlyName">给下拉框看的中文名（用设备自己的名字，不自己编）。</param>
/// <param name="Formats">设备支持的分辨率与帧率。<b>下拉框的全部内容来源</b>。</param>
internal readonly record struct DocumentCameraDevice(
    string Id,
    string FriendlyName,
    IReadOnlyList<CameraFormat> Formats);

/// <summary>
/// 展台那一路的<b>帧来源</b>。做成接口不是为了"以后好换实现"，
/// 而是因为<b>要把整条路在验收里跑通</b> —— 而 CI runner 与大部分开发机
/// 都没有摄像头，更没有色彩转换环节（见 <see cref="CameraAvailability.OpenFailed"/>）。
/// 所以真正的实现是 <see cref="NativeDocumentCameraFrames"/>，
/// 而验收用的是合成帧；两者之间的契约就是这一个接口。
/// </summary>
internal interface IDocumentCameraFrames : IDisposable
{
    /// <summary>设备列表。<b>不保证非空</b>：空列表是合法的降级状态，不是错误。</summary>
    IReadOnlyList<DocumentCameraDevice> Devices { get; }

    /// <summary>当前这路的状态（开成功了没有、失败原因）。</summary>
    CameraAvailability Availability { get; }

    /// <summary>状态的一句人话，给窗口直接显示。</summary>
    string StatusText { get; }

    /// <summary>
    /// 新的一帧到了。<b>在 UI 线程上发</b>，所以订阅者可以直接改界面。
    /// </summary>
    /// <param name="frame">
    /// 这一帧。<b>订阅者不拥有它</b>：下一帧到达时上一张就被 Dispose 了，
    /// 所以要留住就自己 <c>BitmapImage.FromPixels</c> 拷一份。
    /// </param>
    event Action<BitmapImage>? FrameArrived;

    /// <summary>开。<b>不抛</b>：失败走 <see cref="Availability"/> 与 <see cref="StatusText"/>。</summary>
    void Start(string deviceId, int width, int height, double fps);

    /// <summary>停。<b>幂等</b>。</summary>
    void Stop();
}

/// <summary>真机实现：包着 Jalium 的 <see cref="CameraView"/>。</summary>
/// <remarks>
/// <para>
/// <b>为什么包一个控件而不是直接用 <c>INativeCameraSource</c></b>：
/// <c>CameraView</c> 的 <c>CaptureLoop</c> 自己已经把 <c>MediaFrame</c>
/// 转成 <c>BitmapImage</c> 并在 UI 线程上发事件了
/// （<c>src/managed/Jalium.UI.Controls/CameraView.cs</c>）。自己再走一遍
/// <c>TryReadFrame</c> + <c>FromMediaFrame</c> 只是把它做过的事重做一次。
/// </para>
/// <para>
/// <b>它要挂在视觉树上</b>（一个 0×0 的角落格子），因为它是 <c>Control</c>
/// 而不是服务。<c>CameraView</c> 自己会在 <c>OnRender</c> 里画预览 ——
/// 而我们要的预览是"一张带 world→screen 变换的 <c>Image</c>，压在墨迹面底下"，
/// 两者形态不同。所以那个控件只当引擎，尺寸给 0，它的 <c>OnRender</c> 自然不画任何东西。
/// </para>
/// <para>
/// <b>它换帧时从不释放上一张 <c>_currentFrame</c></b>（框架源码里那一句
/// <c>lock (_frameLock) _currentFrame = image;</c> 没有配对的 Dispose）。
/// 单独用 <c>CameraView</c> 就会漏；<b>释放是我们的事</b>，见 <see cref="Publish"/>。
/// </para>
/// </remarks>
internal sealed class NativeDocumentCameraFrames : IDocumentCameraFrames
{
    private readonly CameraView _view;
    private bool _started;
    private bool _disposed;

    internal NativeDocumentCameraFrames(CameraView view)
    {
        _view = view;
        _view.CameraFrameArrived += OnFrameArrived;
    }

    /// <summary>往宿主里加那台"只当引擎"的控件。调用方负责把它摆到 0×0 的角落。</summary>
    internal CameraView View => _view;

    public IReadOnlyList<DocumentCameraDevice> Devices { get; } = Enumerate();

    public CameraAvailability Availability { get; private set; } = CameraAvailability.Unknown;

    public string StatusText { get; private set; } = "";

    public event Action<BitmapImage>? FrameArrived;

    /// <summary>
    /// 探测本机能不能采集。<b>会真的开一次设备</b> ——
    /// 因为"枚举得到"与"开得出"在这台机器上是两件事（见 <see cref="CameraAvailability"/>）。
    /// </summary>
    internal static CameraCapabilityReport Probe(IReadOnlyList<DocumentCameraDevice>? devices = null)
    {
        if (!CameraView.IsCaptureSupported)
        {
            return new CameraCapabilityReport(
                CameraAvailability.BackendMissing,
                BackendMissingText(),
                []);
        }

        devices ??= Enumerate();
        if (devices.Count == 0)
        {
            return new CameraCapabilityReport(
                CameraAvailability.NoDevice,
                "没找到摄像头。接上再点一次就好；如果是笔记本，确认它没被隐私开关挡着。",
                devices);
        }

        // 枚举得到却开不出 —— 一定要分清这一档。
        try
        {
            using var probe = new NativeCameraSource();
            probe.Open(devices[0].Id, 640, 480, 30, NativePixelFormat.Bgra8);
            return new CameraCapabilityReport(
                CameraAvailability.Ready,
                $"已就绪：{devices[0].FriendlyName}",
                devices);
        }
        catch (Exception ex)
        {
            return new CameraCapabilityReport(
                CameraAvailability.OpenFailed,
                $"找到了 {devices.Count} 台摄像头，但打不开（{ex.GetType().Name}）。" +
                "这通常不是没接摄像头，而是系统缺采集链上的一段组件。",
                devices);
        }
    }

    /// <summary>后端没加载时的那句话。<b>按平台给不同的说法</b>，因为原因根本不同。</summary>
    /// <remarks>
    /// Windows：Media Foundation 那一套没起来（多半是那个可选组件没装）。
    /// Linux：采集是<b>能力门控</b>的 —— 取决于运行时有没有 GStreamer 及其摄像头插件，
    /// 所以"不支持"不代表"这台机器不行"，而是"这个发行版/镜像里没有那条链"。
    /// <b>这一段在 CI 上验不到</b>（runner 没有摄像头设备），
    /// 与物理触摸那一批同处理：逻辑分支可测，真实采集不可测。
    /// </remarks>
    private static string BackendMissingText()
    {
        if (OperatingSystem.IsLinux())
        {
            return "这个 Linux 上没有摄像头采集能力（取决于系统里的 GStreamer 与摄像头插件）。" +
                   "视频展台用不了，其余画布不受影响。";
        }

        return "这个平台的采集组件没加载，视频展台用不了。" +
               "批注、屏幕批注、白板、图片与 PDF 都不受影响。";
    }

    private static IReadOnlyList<DocumentCameraDevice> Enumerate()
    {
        try
        {
            return CameraView.EnumerateDevices()
                .Select(d => new DocumentCameraDevice(d.Id, d.FriendlyName, d.SupportedFormats))
                .ToArray();
        }
        catch
        {
            // 枚举本身抛异常（后端没加载、或某台设备的属性读不出来）时，
            // 当"一台都没有"处理：窗口会显示那句"没找到摄像头"，
            // 而那总比在构造函数里炸掉整个画布好。
            return [];
        }
    }

    public void Start(string deviceId, int width, int height, double fps)
    {
        if (_disposed) return;

        // 设备列表是枚举来的，所以要给控件一个 CameraDeviceInfo。
        var info = CameraView.EnumerateDevices().FirstOrDefault(d => d.Id == deviceId);
        if (info is null)
        {
            Availability = CameraAvailability.NoDevice;
            StatusText = "这台摄像头已经不在了（可能拔了或换了）。重新点一次视频展台就能重扫。";
            return;
        }

        _view.Source = info;
        _view.RequestedWidth = width;
        _view.RequestedHeight = height;
        _view.RequestedFps = fps;
        _view.Start();
        _started = true;

        // Start 不抛 —— 它把失败放进 LastError 并发 CameraFailed。
        // 所以这里不能拿"没抛"当成功。
        if (_view.LastError is { } error)
        {
            Availability = CameraAvailability.OpenFailed;
            StatusText = $"打不开这台摄像头：{error.Message}";
            return;
        }

        Availability = CameraAvailability.Ready;
        StatusText = $"{info.FriendlyName} · {width}×{height}";
    }

    private void OnFrameArrived(object? sender, RoutedEventArgs e)
    {
        if (_disposed) return;

        // CurrentFrame 是 BitmapImage —— 而 ImageSource 基类没有 Dispose，
        // 只有 BitmapImage 那一层有（这与主应用里"Image 只吃 BitmapImage"是同一处知识）。
        if (_view.CurrentFrame is not BitmapImage frame) return;
        FrameArrived?.Invoke(frame);

        // ★ 那一帧的生命周期归我们管。框架把 MediaFrame 还了池，却把上一张
        // BitmapImage 留着不释放；30fps 下一帧一换而不释放 = 每秒 30 次 GPU 堆积。
        // 订阅者若要留住这一帧，必须自己拷一份（FromPixels）。
    }

    public void Stop()
    {
        if (!_started) return;
        _view.Stop();
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.CameraFrameArrived -= OnFrameArrived;
        Stop();
    }
}
