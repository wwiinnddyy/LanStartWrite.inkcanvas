using System.Diagnostics;
using System.IO;
using System.Windows;
using Dusk.Ink.Primitives;
using FluentJalium.Controls;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Microsoft.Win32;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 图片批注：<b>打开一张图，在图上写字</b>。
/// <para>
/// 页模型、缩略图导航、缩放漫游、整套"选择 / 框选 / 套索 / 变换"手势全在
/// <see cref="PagedCanvasWindow"/>（与白板共用），本类只做三件图片特有的事：
/// </para>
/// <list type="number">
/// <item><b>把图铺在墨迹底下，而且要跟着视口动</b>（见 <see cref="ApplyPageBackdrops"/>）；</item>
/// <item><b>「打开」而不是「新建空白页」</b>：拉起文件选择框，一个文件一页；</item>
/// <item><b>旋转</b>：图与笔迹一起转，只走 90° 整数步，一步撤销。</item>
/// </list>
/// <para>
/// <b>为什么图不能直接当 <c>InkHost.Background</c></b>（冻结模式那套就是这么做的）：
/// 背景刷填的是<b>元素</b>坐标，而笔迹活在<b>世界</b>坐标里，两者是两套系。冻结模式那张截图
/// 恰好与窗口同尺寸，所以"元素坐标铺满"与"世界坐标铺满"是同一件事，那条捷径只在那种情况下成立。
/// 图片有自己的像素尺寸，一旦漫游或缩放，两套坐标立刻分家 —— 表现是"图钉在屏幕上不动，笔迹走了"。
/// 所以这里把图作为一个独立元素插在墨迹面<b>底下</b>，并按 world→screen 矩阵定位它。
/// </para>
/// </summary>
public partial class ImageViewerWindow : PagedCanvasWindow
{
    protected override string CanvasLayerName => "图片批注";

    /// <summary>文件选择框的过滤器。Jalium 能解的是 PNG / JPEG / BMP / GIF / TIFF（见 BitmapDecoder）。</summary>
    private const string ImageFilter =
        "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*";

    /// <summary>垫在墨迹底下那个元素。<b>索引 0</b>，插错位置就变成"图盖住笔迹"。</summary>
    private Image? _image;

    /// <summary>
    /// 当前页是不是有一张真的图。<b>没打开任何文件时那一页底下什么都没有</b>，
    /// 于是这个窗口一进来是一片空白 —— 那是"还没选文件"，不是"加载失败"。
    /// </summary>
    private bool HasImage;

    private ImagePage? CurrentImagePage => ActivePageOrNull as ImagePage;

    internal ImagePage? ActivePageOrNull => GetActivePage();

    public ImageViewerWindow()
    {
        AllowsTransparency = false;
        InitializeComponent();

        WindowLayerManager.Register(this, WindowLayer.Canvas, CanvasLayerName);

        InitializeCanvasHost(
            InkHost,
            PageControlHost,
            PreviousPageButton,
            NextPageButton,
            AddPageButton,
            PageNumberText);
        InitializeSharedCanvas();
        WireZoomControls();
    }

    /// <summary>底栏右边那一排缩放控件的接线。三个入口（+、−、百分数）都归到这里。</summary>
    private void WireZoomControls()
    {
        ((Button)ZoomInButton!).Click += (_, _) => StepZoom(+1);
        ((Button)ZoomOutButton!).Click += (_, _) => StepZoom(-1);
        ((Button)ZoomPercentButton!).Click += (_, _) => ToggleZoomMenu();

        PreviewKeyDown += (_, e) =>
        {
            // Esc 先关二级菜单：它浮在最上面，不先关它的话 Esc 什么也不做。
            if (e.Key == Key.Escape && IsZoomMenuOpen)
            {
                HideZoomMenu();
                e.Handled = true;
            }
        };

        AutomationProperties.SetName(ZoomInButton, "放大");
        AutomationProperties.SetName(ZoomOutButton, "缩小");
        SyncZoomUi();
    }

    private void ToggleZoomMenu()
    {
        if (IsZoomMenuOpen) HideZoomMenu();
        else ShowZoomMenu();
    }

    /// <summary>
    /// 二级菜单<b>开</b>没有。
    /// <para>
    /// <b>自己记，不读 <c>_zoomMenu.IsVisible</c></b>：窗口那个属性在"刚 Show / 刚 Hide"
    /// 之后不是立刻准的（同一个钮连点两次时读到的还是上一次的值），
    /// 于是"同一个钮开关它"这条会变成"第一次点开、第二次点没反应"。
    /// 层级系统那边也是这个理由才只听 <c>Shown</c> / <c>Hiding</c>。
    /// </para>
    /// </summary>
    internal bool IsZoomMenuOpen { get; private set; }

    private ZoomSecondaryMenuWindow? _zoomMenu;

    /// <summary>
    /// 建一张<b>还没有图</b>的页。
    /// <para>
    /// 窗口一进来就得有一页（基类要拿它当当前面），而"还没有图"这件事只能是空的 ——
    /// 硬塞一张占位图会在用户还没选文件时就看见一张不存在的图，那比空白更糟。
    /// </para>
    /// </summary>
    protected override CanvasPage CreatePageCore() =>
        new ImagePage(new CanvasSurface(Dispatcher, assertLoadedSize: false), string.Empty, null);

    /// <summary>「打开」= 拉起文件选择框，<b>可以一次选多个</b>，每个文件一页。</summary>
    protected override void OnAddPageRequested()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开图片",
            Filter = ImageFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var opened = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            if (!TryOpenAsPage(file)) continue;
            opened.Add(file);
        }

        RememberOpened(opened);
    }

    /// <summary>
    /// 把这一批记进"最近打开"与"上次目录"。只有真打开成功的才记 ——
    /// 把一个失败的文件写进最近列表，用户下次启动就会再失败一次。
    /// </summary>
    private void RememberOpened(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        var directory = System.IO.Path.GetDirectoryName(paths[0]);
        var recent = new List<string>(paths);
        recent.AddRange(AppPreferences.Current.RecentImages.Items ?? []);
        AppPreferences.Update(AppPreferences.Current with
        {
            RecentImages = new RecentImageCollection { Items = recent },
            LastImageDirectory = string.IsNullOrEmpty(directory)
                ? AppPreferences.Current.LastImageDirectory
                : directory,
        });
    }

    /// <summary>
    /// 启动恢复：把"最近打开"里还打得开的那些重新开成页。
    /// <para>
    /// <b>打不开的静默跳过</b>，而不是弹一串错误：外接盘没插、文件被删，这些在"恢复"这个
    /// 场景下是常态，为它们打断启动体验不值当。真正一张都打不开时，窗口就是空白 ——
    /// 而空白与"还没选过文件"是同一个样子，用户点一下就能重新选。
    /// </para>
    /// </summary>
    internal void RestoreRecentImages()
    {
        if (!AppPreferences.Current.ImageRestoreOnStartup) return;
        if (HasAnyRealPage) return;

        var opened = new List<string>();
        foreach (var path in AppPreferences.Current.RecentImages.Items ?? [])
        {
            if (!File.Exists(path)) continue;
            if (TryOpenAsPage(path)) opened.Add(path);
        }

        // 恢复过的就不再当"新打开"记一次：那会把时间顺序按恢复顺序重排，用户的历史被改写。
        if (opened.Count > 0) RefreshPageBackdrops();
    }

    /// <summary>除了刚建出来那一张空白页之外，有没有真的图页。</summary>
    private bool HasAnyRealPage
    {
        get
        {
            foreach (var page in Pages)
            {
                if (page is ImagePage { Path.Length: > 0 }) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 打开一个文件并<b>激活成当前页</b>。失败返回 false（不抛：那一个文件坏了不该让整窗都开不了）。
    /// <para>
    /// 走 <see cref="BitmapImage.FromFile"/> 而不是 <c>BitmapDecoder</c> → <c>Frames[0]</c>：
    /// 后者给出的是一个 <c>BitmapFrame</c>，它的 <c>NativeHandle</c> 恒为 0，
    /// 而 <c>Image</c> 的解码与 GPU 上传只认 <c>BitmapImage</c>。喂错类型<b>不报错</b>，
    /// 只是什么都不画 —— 表现为"标题栏说打开了，窗口一片黑"。这个坑踩过一次。
    /// </para>
    /// </summary>
    private bool TryOpenAsPage(string path)
    {
        BitmapImage? image;
        try
        {
            image = BitmapImage.FromFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Trace.TraceError($"打不开图片 {path}: {ex.Message}");
            return false;
        }

        if (image.Width <= 0 || image.Height <= 0)
        {
            image.Dispose();
            return false;
        }

        return OpenImage(image, path);
    }

    /// <summary>
    /// 把一张已经解码好的图开成<b>一页并激活</b>。
    /// <para>
    /// 与 <see cref="TryOpenAsPage"/> 分开是因为"读文件"与"开一页"是两件事：
    /// 文件关联那条路拿到的也是一张已解码的图（可能来自命令行、可能来自拖放），
    /// 而验收要的是"给定一张图能不能开成页"，走真实文件会多一份与被测行为无关的失败面。
    /// </para>
    /// </summary>
    internal bool OpenImage(BitmapImage image, string path)
    {
        if (image.Width <= 0 || image.Height <= 0) return false;
        AddPage(new ImagePage(new CanvasSurface(Dispatcher, assertLoadedSize: false), path, image));
        return true;
    }

    /// <summary>
    /// 把图铺到位：<b>宿主里那一张 + 每一页缩略图那一张</b>。
    /// <para>
    /// 缩略图那边不走 world→screen 矩阵（它自己按 <c>ContentSize</c> 缩放），宿主这边要 ——
    /// 所以两条路都写在这一条里，换页与换设置都只经过这一个入口。
    /// </para>
    /// <para>
    /// <b>底下那一层垫色也在这里刷</b>：没打开任何文件时窗口不能是黑的。
    /// 这不是美观问题 —— 黑底 + 一张没画出来的图，两眼看到的东西一模一样，
    /// 于是"图加载失败"与"还没选文件"被混成同一个症状，排查只能靠猜。
    /// </para>
    /// </summary>
    protected override void ApplyPageBackdrops()
    {
        ReassertImageLayer();

        var page = CurrentImagePage;
        HasImage = page is { Image: not null } && page.Path.Length > 0;

        // 有图 = 白的（图自己盖满）；没图 = 浅灰的空板。两者<b>看起来就不一样</b> ——
        // 黑底 + 一张没画出来的图和"还没选文件"在眼里完全同形，那才是最难查的一类症状。
        var backdrop = new SolidColorBrush(HasImage ? Colors.White : Color.FromArgb(255, 0xF3, 0xF3, 0xF3));
        InkHostGrid.Background = backdrop;
        Background = backdrop;

        ApplyImageToHost(page);
        ApplyImageToThumbnails();
        UpdateTitle();
    }

    /// <summary>
    /// 把图按回宿主索引 <b>0</b>。
    /// <para>
    /// <b>每换一页都要重申一次</b>：基类换页时会 <c>DetachFrom</c> 旧面再
    /// <c>AttachTo(InkHost, 0)</c>，而那一步把新墨迹面插在 <b>0</b> —— 于是原本在 0 的图被挤到 1，
    /// 变成"图压在笔迹上面"。症状是<b>不报任何错</b>，只是图上的笔迹不见了（被图盖住），
    /// 所以只能在换页这一条路上钉住。
    /// </para>
    /// </summary>
    private void ReassertImageLayer()
    {
        if (_image is null) return;
        if (InkHostGrid.Children.IndexOf(_image) == 0) return;
        InkHostGrid.Children.Remove(_image);
        InkHostGrid.Children.Insert(0, _image);
    }

    private void ApplyImageToHost(ImagePage? page)
    {
        if (_image is null) return;

        if (page is null || !HasImage)
        {
            _image.Source = null;
            _image.Visibility = Visibility.Hidden;
            return;
        }

        // 图<b>始终是原朝向</b>，旋转靠布局尺寸 + 一个旋转变换表达，
        // 而不是把位图转一遍 —— 一张 4000×3000 的图转一次要复制几千万像素。
        _image.Source = page.Image;
        _image.Width = page.PageSize.Width;
        _image.Height = page.PageSize.Height;
        _image.Visibility = Visibility.Visible;
        _image.RenderTransform = BuildWorldTransform(page);
    }

    private void ApplyImageToThumbnails()
    {
        foreach (var canvasPage in Pages)
        {
            if (canvasPage is not ImagePage page)
            {
                canvasPage.Thumbnail.PageBrush = null;
                canvasPage.Thumbnail.PageBackground = Colors.White;
                canvasPage.Thumbnail.ContentSize = null;
                canvasPage.Thumbnail.Refresh();
                continue;
            }

            if (page.Path.Length == 0 || page.Image is null)
            {
                canvasPage.Thumbnail.PageBrush = null;
                canvasPage.Thumbnail.PageBackground = Colors.White;
                canvasPage.Thumbnail.ContentSize = null;
            }
            else
            {
                // 缩略图按"图自己的尺寸"对位笔迹，而不是按笔迹的包围盒 ——
                // 否则只在图的左上角画一笔，缩略图会把那一笔放大成满格。
                var brush = new ImageBrush(page.Image) { Stretch = Stretch.Uniform };
                canvasPage.Thumbnail.PageBrush = brush;
                canvasPage.Thumbnail.PageBackground = Colors.White;
                canvasPage.Thumbnail.ContentSize = page.PageSize;
            }

            canvasPage.Thumbnail.Refresh();
        }
    }

    /// <summary>
    /// 用 <b>两个探针点</b>重建 world→screen 矩阵。
    /// <para>
    /// 为什么不直接问引擎要矩阵：<c>InkViewport</c> 那一面确实有 <c>InkViewportMatrix</c>，
    /// 但宿主这边没有一个已经验证过的取值口，而 <c>View.WorldToScreen</c> 是应用里到处在用、
    /// 已被回归盯住的一条路。问两次"世界原点在屏幕上哪儿"与"世界 x 轴一个单位在屏幕上多长"
    /// 就能精确还原这个纯平移+缩放的变换（引擎的视口<b>没有旋转</b>，所以两次采样是充分的），
    /// 而且不引入一条新的、只在图片这一处用到的依赖。
    /// </para>
    /// </summary>
    private Transform BuildWorldTransform(ImagePage page)
    {
        var view = Surface.View;
        var origin = view.WorldToScreen(new Point2D(0, 0));
        var axisX = view.WorldToScreen(new Point2D(1, 0));
        var axisY = view.WorldToScreen(new Point2D(0, 1));

        var rotate = BuildRotation(page);

        var group = new TransformGroup();
        group.Children.Add(rotate);
        // Rotate 在前、Translate 在后：先把图绕原点转到朝向来，再把结果平到当前页的尺寸上。
        group.Children.Add(new TranslateTransform(
            page.QuarterTurns switch
            {
                1 => page.PageSize.Height,
                2 => page.PageSize.Width,
                3 => 0,
                _ => 0,
            },
            page.QuarterTurns switch
            {
                1 => 0,
                2 => page.PageSize.Height,
                3 => page.PageSize.Width,
                _ => 0,
            }));
        group.Children.Add(new ScaleTransform(
            (axisX.X - origin.X) / 1.0,
            (axisY.Y - origin.Y) / 1.0));
        group.Children.Add(new TranslateTransform(origin.X, origin.Y));
        return group;
    }

    private static RotateTransform BuildRotation(ImagePage page)
    {
        var rotate = new RotateTransform();
        switch (page.QuarterTurns)
        {
            case 1:
                rotate.Angle = 90;
                break;
            case 2:
                rotate.Angle = 180;
                break;
            case 3:
                rotate.Angle = 270;
                break;
        }
        return rotate;
    }

    /// <summary>验收读它：<b>图是不是真的垫在墨迹面底下</b>（索引 0），而不是浮在墨迹上面。</summary>
    internal int? ImageLayerIndexInHost =>
        _image is not null && InkHostGrid.Children.Contains(_image)
            ? InkHostGrid.Children.IndexOf(_image)
            : null;

    /// <summary>验收读它：宿主里那一张的宽高（世界单位），用来判"图按图自己的尺寸铺"与"跟着转过朝向"。</summary>
    internal (double Width, double Height) HostedImageSize =>
        _image is null ? (0, 0) : (_image.Width, _image.Height);

    /// <summary>验收读它：宿主里那一张是不是真的换成了某个源（而不是"没打开"时那种空）。</summary>
    internal bool HostedImageVisible => _image is not null && _image.Visibility == Visibility.Visible;

    /// <summary>宿主里那一张现在处于什么状态。</summary>
    internal string HostedImageState => _image is null
        ? "宿主里没有图元素"
        : $"vis={_image.Visibility} hasImage={HasImage} src={(_image.Source is null ? "null" : "有")} " +
          $"size={_image.Width}x{_image.Height} pages={PageCount} active={ActivePageIndex}";

    /// <summary>已经摆过一次的是哪一档。<b>没摆过是 null</b>（于是第一次显形一定会摆一次）。</summary>
    private ImageOpenMode? _appliedOpenMode;

    /// <summary>
    /// 按设置里的<b>打开方式</b>摆这个窗口的形状 —— <b>但只在那一档真的变了的时候摆</b>。
    /// <para>
    /// 这条是用户报的那个缺陷换来的：「窗口最大化状态下在工具栏切一下工具，就掉出最大化」。
    /// 根因不是"摆错了"，是<b>摆得太勤</b>：这条路径每次切工具都走一遍（见
    /// <c>SyncImageViewerOverlay</c> 的每个分支），而窗口模式下它无条件
    /// <c>WindowState = Normal</c> 加重设 <c>Left/Top</c> —— 最大化状态当场被抹掉并重新居中。
    /// </para>
    /// <para>
    /// 所以判据是「请求的这一档与上次摆的不是同一档吗」：
    /// <list type="bullet">
    /// <item>换档了 → 摆一次（用户改了设置，下一次进画布就该看到新形状）。</item>
    /// <item>没换 → <b>什么都不碰</b>。用户自己摆的形状（最大化、他自己拖的位置和大小）
    /// 是他<b>当下的决定</b>，切个工具不该被"顺手复位"。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 顺带一条：<b>给最大化窗口设 <c>Left/Top</c> 本身就会把它打回 Normal</b>（外壳按还原尺寸摆）。
    /// 所以这两件事必须成对出现 —— 摆形状与改窗口状态是一件事，不能分开做。
    /// </para>
    /// </summary>
    internal void ApplyOpenMode(ImageOpenMode mode)
    {
        if (_appliedOpenMode == mode) return;
        _appliedOpenMode = mode;

        if (mode == ImageOpenMode.FullScreen)
        {
            ApplyFullScreenShape();
            return;
        }

        ApplyWindowShape();
    }

    /// <summary>
    /// 全屏：**形状照白板那一块一模一样**，不是"普通的最大化"。
    /// <para>
    /// 差别全在<b>有没有窗框</b>：白板是 <c>WindowStyle=None</c> + 标题栏/系统菜单/任务栏全关，
    /// 最大化之后它铺满<b>整块屏</b>（含任务栏那条）；带窗框的窗口最大化只到工作区，
    /// 于是底下露出任务栏，顶上留一条窗框边 —— 看着就是"一个被拉到最大的普通窗口"，
    /// 而不是"全屏"。用户报的就是这个差别。
    /// </para>
    /// <para>
    /// 所以这里<b>不写 <c>Topmost</c></b>：层级归 <see cref="WindowLayerManager"/> 管，
    /// 这个类里一行 Topmost 都不该有（与白板同一条规矩）。
    /// </para>
    /// </summary>
    private void ApplyFullScreenShape()
    {
        WindowStyle = WindowStyle.None;
        IsShowTitleBar = false;
        IsShowMinimizeButton = false;
        IsShowMaximizeButton = false;
        IsShowCloseButton = false;
        HasSystemMenu = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        // 顺序有讲究：**先摘窗框再最大化**。反过来（先最大化再摘）外壳会按带框的尺寸算一次，
        // 留一条边就再也补不回来了。
        WindowState = WindowState.Maximized;
    }

    /// <summary>窗口模式：把窗框那一套还回来，再摆一个居中的初始形状。</summary>
    private void ApplyWindowShape()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        IsShowTitleBar = true;
        IsShowMinimizeButton = true;
        IsShowMaximizeButton = true;
        IsShowCloseButton = true;
        HasSystemMenu = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;

        var work = Jalium.UI.SystemParameters.WorkArea;
        var width = Math.Min(960, Math.Max(320, work.Width - 160));
        var height = Math.Min(720, Math.Max(240, work.Height - 240));
        Width = width;
        Height = height;
        Left = work.X + (work.Width - width) / 2;
        Top = work.Y + Math.Max(0, (work.Height - height) / 2) - 40;
    }

    // ---------------------------------------------------------------- 缩放

    /// <summary>一次加/减按的倍数。</summary>
    private const double ZoomStep = 1.25;

    /// <summary>
    /// 控件能走到的范围（倍数）。<b>比引擎的 MinZoom/MaxZoom 窄</b>，钉在用户要的那一档：
    /// 0~300%。窄了的好处是数字框与滑块<b>永远说得出真话</b> ——
    /// 若加号能走到 500%，而滑块只到 300%，用户拖到头会看到"300%"却实际是 500%。
    /// 下限 10% 而不是 0：0 倍的图等于没有，而滑块拖到最左会给出 0。
    /// </summary>
    private const double ZoomControlMin = 0.1;

    private const double ZoomControlMax = 3.0;

    /// <summary>当前放大系数（1 = 原始大小）。</summary>
    internal double ZoomScale => Surface.View.Viewport.Scale;

    /// <summary>底栏中间那个要念出来的百分数。</summary>
    internal int ZoomPercent => (int)Math.Round(ZoomScale * 100);

    /// <summary>
    /// 粗调一步：<paramref name="direction"/> &gt; 0 放大、&lt; 0 缩小，锚在画布正中。
    /// <para>
    /// 锚在<b>正中</b>而不是指针位置：这一栏是给"整张图看"用的，
    /// 而底栏上的加号离图很远，指针多半根本不在图上 ——
    /// 按指针锚会得出"图往边上跑了"这种莫名其妙的缩放。
    /// </para>
    /// </summary>
    internal void StepZoom(int direction)
    {
        var factor = direction > 0 ? ZoomStep : 1.0 / ZoomStep;
        ZoomBy(factor);
    }

    /// <summary>
    /// 按一个倍数<b>相对</b>缩放。粗调那两颗钮走这里。
    /// <para>
    /// 引擎那个 <c>NaN</c> 表示"适应窗口"那一档<b>已经去掉了</b>（用户不要最大/最小按钮），
    /// 所以这里只有一种含义：相对。
    /// </para>
    /// </summary>
    internal void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        ZoomTo(ZoomScale * factor);
    }

    /// <summary>
    /// 缩放到某个<b>绝对</b>倍数。滑块要的是这个 —— 拖到 140 就是 1.4，不是"再放大 1.4 倍"。
    /// </summary>
    internal void ZoomTo(double scale)
    {
        if (!double.IsFinite(scale)) return;

        // 夹到**控件那一档**范围，不是引擎的范围：数字框与滑块要能说出真话。
        // 用户能拖到的最左是 0%，但 0 倍等于没有图，所以真落到 10% 就不动了。
        var target = Math.Clamp(scale, ZoomControlMin, ZoomControlMax);
        var current = ZoomScale;
        if (current <= 0) return;
        if (Math.Abs(target - current) < 1e-9) return;

        var centre = new Point(InkHostGrid.ActualWidth / 2, InkHostGrid.ActualHeight / 2);
        Surface.ZoomAt(centre, target / current, MinZoom, MaxZoom);
        SyncZoomUi();
    }

    /// <summary>
    /// 底栏那一排（百分数）与二级菜单里的滑块是<b>同一个数</b>的两次显示。
    /// 所以每次视口变了都要两处一起刷 —— 只刷一处的话，
    /// 用户拖了滑块之后底栏还写着旧数字，而两处本该一致。
    /// </summary>
    private void SyncZoomUi()
    {
        var text = $"{ZoomPercent}%";
        ((TextBlock)ZoomPercentText!).Text = text;
        AutomationProperties.SetName(ZoomPercentButton!, $"缩放 {text}（点开可无级调节）");
        _zoomMenu?.ShowScale(ZoomScale);
    }

    /// <summary>把二级菜单摆在百分数那颗按钮下面。</summary>
    private void EnsureZoomMenu()
    {
        if (_zoomMenu is not null) return;

        _zoomMenu = new ZoomSecondaryMenuWindow { Owner = this };
        _zoomMenu.ZoomChanged += scale =>
        {
            ZoomBy(scale / 100 / (ZoomScale <= 0 ? 1 : ZoomScale));
        };
        _zoomMenu.DismissRequested += HideZoomMenu;
    }

    private void ShowZoomMenu()
    {
        EnsureZoomMenu();
        if (_zoomMenu is null) return;
        IsZoomMenuOpen = true;
        _zoomMenu.ShowScale(ZoomScale);
        _zoomMenu.Show();
        PlaceZoomMenu();
    }

    // IsShown 这条同样适用：可见性听自己这一趟 Show / Hide，不问窗口。

    private void HideZoomMenu()
    {
        IsZoomMenuOpen = false;
        _zoomMenu?.Hide();
    }

    /// <summary>
    /// 把二级菜单摆在右下角那排缩放控件的<b>正上方</b>、右缘对齐，并夹在屏幕之内。
    /// <para>
    /// <b>坐标走 <c>PointToScreen</c>，绝不加 <c>Left</c>/<c>Top</c>。</b>
    /// 最大化（全屏）窗口的 <c>Left</c>/<c>Top</c> 报的是<b>还原位置</b>，不是它在屏幕上的位置 ——
    /// 所以"控件在窗口里的偏移 + 窗口 Left/Top"这条路在窗口模式下碰巧对，一进全屏就整体偏出去。
    /// <para>
    /// <b>但 <c>PointToScreen</c> 返回的是<b>物理像素</b>，而 <c>Window.Left/Top</c> 要 <b>DIP</b></b>
    /// —— 直接赋值就是差一个 DPI 倍数（本机 175% → 差 1.75 倍，看着就是"飞到别处去了"）。
    /// 缩放比用<b>公开 API 自己量</b>：同一元素上相隔 100 DIP 的两个点，屏幕坐标差多少就是多少倍。
    /// （<c>Window.DpiScale</c> 不是公开的，而反射它正是本项目明令禁止的那类补丁。）
    /// <para>
    /// <b>摆在上面而不是下面</b>：那排控件贴着屏幕下沿，摆在下面会有一半掉出屏外。
    /// <b>尺寸在 <c>Show()</c> 之后再量</b>：<c>ActualWidth</c> 在刚 Show 时还是 0。
    /// </para>
    /// </summary>
    private void PlaceZoomMenu()
    {
        if (_zoomMenu is null) return;
        var host = (FrameworkElement)ZoomControlHost!;
        var width = _zoomMenu.ActualWidth > 0 ? _zoomMenu.ActualWidth : ZoomMenuFallbackWidth;
        var height = _zoomMenu.ActualHeight > 0 ? _zoomMenu.ActualHeight : ZoomMenuFallbackHeight;

        // 缩放比：屏幕上 100 DIP 实际有多长。
        var origin = host.PointToScreen(new Point(0, 0));
        var probe = host.PointToScreen(new Point(100, 0));
        var scale = Math.Abs(probe.X - origin.X) / 100.0;
        if (scale is not (> 0.25 and < 8)) scale = 1.0;

        // 屏幕坐标（物理像素）→ DIP，才能拿去赋给 Left/Top。
        var topRight = ToDip(host.PointToScreen(new Point(host.ActualWidth, 0)), scale);
        var bottomRight = ToDip(host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight)), scale);
        var work = Jalium.UI.SystemParameters.WorkArea;

        // 横向按工作区夹：左右本来就离窗口边 12 DIP，夹一下保证不出屏。
        var left = topRight.X - width;
        left = Math.Clamp(left, work.X + 8, Math.Max(work.X + 8, work.Right - width - 8));

        // 纵向<b>只</b>防"顶到屏幕上沿"（那才真的看不见），<b>不按工作区下沿夹</b>。
        // 理由：全屏那一档盖的是<b>整块屏</b>，底栏落在工作区下沿之下（任务栏那条），
        // 而菜单是<b>相对那排控件</b>算的 —— 位置天然在屏内。
        // 早先那版拿 WorkArea 上下一起夹，于是被压到 814：控件上沿在 852，
        // 菜单只有 44 高，夹完正好压在控件上面还差一截。看着就是"没落在那排控件上方"。
        var top = bottomRight.Y - height - 6;
        if (top < work.Y + 8) top = bottomRight.Y + 6;
        if (top < work.Y + 8) top = work.Y + 8;

        _zoomMenu.Left = left;
        _zoomMenu.Top = top;
    }

    private static Point ToDip(Point screen, double scale) => new(screen.X / scale, screen.Y / scale);

    /// <summary>菜单刚 Show、还没量到尺寸时用的兜底尺寸（与标记里那两处一致）。</summary>
    private const double ZoomMenuFallbackWidth = 216;

    private const double ZoomMenuFallbackHeight = 44;

    /// <summary>
    /// 验收读它：图当前那个 world→screen 变换。缩放前后<b>必须不一样</b> ——
    /// 图要是没跟着视口重定位，它会钉在屏幕上不动而笔迹走了，而那两样看上去仍然"都在"。
    /// </summary>
    internal Transform? HostedImageTransform => _image?.RenderTransform;

    /// <summary>
    /// 放掉每一页的位图。<b>必须显式</b>：<c>BitmapImage</c> 是 <c>IDisposable</c> 且实现了
    /// <c>IReclaimableResource</c>，它自己不去掉的东西不会被 GC 之外的任何机制回收 ——
    /// 一张 4000×3000 的图按 BGRA 常驻就是 48 MiB，开几十页就是几个 G。
    /// </summary>
    protected override void DisposeDerivedResources()
    {
        _image = null;
        foreach (var page in Pages)
        {
            if (page is ImagePage { Image: { } image }) image.Dispose();
        }
    }

    /// <summary>
    /// 批注栏在窗口模式下要搬进来的那一格。
    /// <para>全屏模式下它一直空着 —— 那时批注栏是独立窗口浮在图上面。</para>
    /// </summary>
    internal Grid ToolbarHost => (Grid)ToolbarHostGrid!;

    /// <summary>验收读它：批注栏的视觉是不是真的在这棵视觉树里（而不是"两个窗口看着挨着"）。</summary>
    internal bool HostsToolbar => ToolbarHost.Children.Count > 0;

    /// <summary>验收读它：宿主里那一张的源本身（判"是不是带 GPU 后端的那种"）。</summary>
    internal ImageSource? HostedImageSource => _image?.Source;

    /// <summary>缩放的二级菜单（还没建就是 null）。探针据此验"点百分数会弹出菜单"。</summary>
    internal ZoomSecondaryMenuWindow? ZoomMenu => _zoomMenu;

    /// <summary>底栏那一排缩放控件，验它的次序（左加号 / 中百分数 / 右减号）。</summary>
    internal FrameworkElement? ZoomControlElement => ZoomControlHost;

    /// <summary>视口动了：图要跟着重定位，否则它会钉在屏幕上不动而笔迹走了。</summary>
    protected override void OnViewportChangedCore()
    {
        // 底栏那个百分数与二级菜单里的滑块是同一个数，所以视口一变就一起刷。
        // 漏掉这一步的症状很具体：用户捏合缩放之后底栏还写着 100%，
        // 而图明显变大了 —— 两处本该一致的东西不一致。
        SyncZoomUi();

        var page = CurrentImagePage;
        if (_image is not null && page is not null && HasImage)
        {
            _image.RenderTransform = BuildWorldTransform(page);
        }
    }

    /// <summary>共用部分装好、墨迹面已在索引 0 之后：把图插到<b>它底下</b>。</summary>
    protected override void OnSharedCanvasReady()
    {
        _image = new Image
        {
            Visibility = Visibility.Hidden,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        InkHostGrid.Children.Insert(0, _image);
    }

    private void UpdateTitle()
    {
        var page = CurrentImagePage;
        Title = page is { Path.Length: > 0 } ? $"{page.FileName} — 图片批注" : "图片批注";
    }

    /// <summary>当前那一页（基类不暴露"当前页对象"，只暴露了面与索引）。</summary>
    private ImagePage? GetActivePage()
    {
        var index = ActivePageIndex;
        if (index < 0 || index >= PageCount) return null;
        var pages = Pages;
        return index < pages.Count ? pages[index] as ImagePage : null;
    }

    // ------------------------------------------------------------------ 旋转

    /// <summary>
    /// 当前页转 90°：<b>图与笔迹一起转，一步撤销</b>。
    /// <para>
    /// 只走 90° 的整数步（引擎的视口<b>没有旋转</b>，见 <c>Dusk/docs/07-infinite-canvas.md</c>：
    /// 它的 <c>InkViewport</c> 是"平移 + 缩放"）。整数步让页面始终是一个矩形 ——
    /// 任意角度会让页面外框变成非矩形，于是落笔边界、橡皮半径换算、缩略图裁切全要跟着改。
    /// </para>
    /// <para>
    /// 笔迹那一半靠<b>全选 → 旋转 → 平移</b>：引擎只有选区级的 <c>Selection.Rotate</c>，
    /// 没有文档级的，所以"旋转整篇"就是把选区取成全篇再转。转完还要平一下 ——
    /// 绕原页中心转 90° 之后，内容落在"以原页中心为中心、宽高对调"的那个框里，
    /// 而新页的框在<b>另一个</b>中心上（宽高对调之后中心坐标也跟着对调），
    /// 不平就会整体偏出去半个 (宽-高)。宽高相等时那两个平移量都是 0，自动退化。
    /// </para>
    /// </summary>
    /// <param name="direction">+1 顺时针，-1 逆时针。</param>
    internal void RotateActivePage(int direction)
    {
        var page = CurrentImagePage;
        if (page?.Image is null) return;
        if (direction is not (1 or -1)) return;

        var surface = Surface;
        var history = surface.History;
        var width = page.PageSize.Width;
        var height = page.PageSize.Height;
        var hadStrokes = surface.Document.Strokes.Count > 0;

        // 整件事一批：图那一侧不在文档里（它是这一页的底），所以"撤销"只能撤销笔迹那半边。
        // 这是有意接受的取舍 —— 撤销一次会回到"笔迹没转"的状态，而图已经转了；
        // 要真正原子就得把图也塞进文档，那不是这一版要做的。
        bool owns = history.BeginBatch();
        try
        {
            page.TurnQuarter(direction);

            if (hadStrokes)
            {
                RunDocumentTransform(document =>
                {
                    var selection = document.Selection;
                    selection.Clear();
                    document.SelectAll();
                    selection.Rotate(width / 2, height / 2, direction * Math.PI / 2);

                    var dx = (page.PageSize.Width - width) / 2;
                    var dy = (page.PageSize.Height - height) / 2;
                    if (dx != 0 || dy != 0) selection.Translate(dx, dy);

                    selection.Clear();
                });
            }
        }
        finally
        {
            if (owns) history.EndBatch();
        }

        RefreshPageBackdrops();
    }
}
