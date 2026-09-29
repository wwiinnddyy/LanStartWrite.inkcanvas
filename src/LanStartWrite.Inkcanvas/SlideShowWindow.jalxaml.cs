using System.Collections.Generic;
using Jalium.UI;
using Jalium.UI.Automation;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using LanStartWrite.Inkcanvas.Diagnostics;
using LanStartWrite.Inkcanvas.Slideshow;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 放映批注：<b>一块盖在别人放映画面之上的透明全屏层</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>形状与白板/展台同族，分页模型也与它们同构</b>（一页一世界、翻页换面），
/// 所以整份继承 <see cref="PagedCanvasWindow"/>：页数池、<b>缩略图弹层</b>、缩放漫游、
/// 以及整套"选择 / 框选 / 套索 / 九颗手柄"全在那份里。
/// <para>
/// <b>左下角那个页面控件直接沿用基类那套</b>（上一页 / 页码 / 下一页 + 点页码弹出缩略图），
/// 与白板完全一致。上一版自己另做了一条常驻缩略图条，结果它与基类那个弹层
/// <b>同时</b>在屏幕上，成了两个控件叠在一起，而两条缩略图的数据还各说各话。
/// </para>
/// </para>
/// <para>
/// <b>与其它几块最不同的一点是"底下是什么"：这里是空的。</b>
/// 白板底下是纯色、图片底下是一张位图、展台底下是每帧换一张的视频 ——
/// 而放映时底下是 <b>PowerPoint/WPS 自己正在全屏播放的幻灯片</b>。
/// 铺底就等于把别人的放映盖住，而那块底"看上去没问题"（它确实让墨迹更清楚），
/// 症状却是"我的 PPT 看不见了"。所以 <see cref="ApplyPageBackdrops"/> 什么都不铺。
/// </para>
/// <para>
/// <b>这一阶段跑的是模拟放映</b>（设置页「放映管理 › 预览」）：底下垫一张占位图当幻灯片。
/// 真 PPT 来源从 <see cref="ISlideThumbnailSource"/> 那道缝接进来，<b>本类一行都不用改</b>。
/// </para>
/// </remarks>
public partial class SlideShowWindow : PagedCanvasWindow
{
    /// <summary>层级登记名（"演示批注"）。</summary>
    protected override string CanvasLayerName => "演示批注";

    private ISlideThumbnailSource _source;
    private ISlideShowLink? _link;
    private Jalium.UI.Threading.DispatcherTimer? _poll;

    public SlideShowWindow()
        : this(new SyntheticSlideThumbnails())
    {
    }

    /// <summary>验收与预览走这条路：换一份来源，其余行为一模一样。</summary>
    internal SlideShowWindow(ISlideThumbnailSource source)
    {
        _source = source;

        // 透明 + 不抢焦点：放映时底下是别人的全屏放映，我们要的是"盖一层能写字的透明面"，
        // 而抢焦点会让放映的键盘控制（翻页、退出）落到我们身上。
        AllowsTransparency = true;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        InitializeComponent();

        // 层级登记：与批注、白板、展台同在画布层。这个类里一行 Topmost 都不该有 ——
        // "画布可见即压过其他应用"是层自带的性质（见 WindowLayerManager）。
        WindowLayerManager.Register(this, WindowLayer.Canvas, CanvasLayerName);

        InitializeCanvasHost(
            InkHost,
            PageControlHost,
            PreviousPageButton,
            NextPageButton,
            AddPageButton,
            PageNumberText);
        InitializeSharedCanvas();

        Closed += (_, _) =>
        {
            StopPolling();
            DisposeSharedCanvas();
        };
    }

    /// <summary>
    /// 按当前选中的那一档接上联动，并开始轮询放映页码。
    /// </summary>
    /// <remarks>
    /// <b>换档要把上一档彻底放掉再接新的</b> —— 那一档持有 COM 的 RCW，
    /// 不放就是漏一份引用计数，而症状是"换几次档之后 PowerPoint 内存 steadily 涨"。
    /// </remarks>
    internal void UseLink(SlideShowLinkMode mode)
    {
        StopPolling();
        _link?.Dispose();
        _link = SlideShowLinkCatalog.Create(mode);
        DeckStatusText.Text = string.Empty;
        DeckStatusText.Visibility = Visibility.Collapsed;
        StartPolling();
    }

    private void StartPolling()
    {
        _poll = new Jalium.UI.Threading.DispatcherTimer
        {
            // 250ms：翻页到笔迹跟上的延迟肉眼几乎看不出，而再快就只是白烧 CPU。
            Interval = System.TimeSpan.FromMilliseconds(250),
        };
        _poll.Tick += (_, _) => PollOnce();
        _poll.Start();
        PollOnce();
    }

    /// <summary>停掉轮询。<b>离开放映场景时必须调</b>，理由见调用处。</summary>
    private void StopPolling()
    {
        if (_poll is null) return;
        _poll.Stop();
        _poll = null;
    }

    /// <summary>
    /// 场景是不是还停在放映上。<b>不是就停轮询。</b>
    /// </summary>
    /// <remarks>
    /// <b>这一条是补一个我自己造的洞</b>：原先只有 <c>Closed</c> 才停，
    /// 而那个窗口是<b>缓存复用的</b>（隐藏而不是销毁），所以"隐藏之后"一路轮询下去 ——
    /// 后果有两个：每秒四趟 COM 白烧，以及**用户下一次自己按 F5 放映时，
    /// 这个已经隐藏的窗口会把那一行「COM 连上」写进日志**，
    /// 于是日志上出现"读到了却没进放映"这种自相矛盾的行（我为这条查了一轮）。
    /// </remarks>
    internal void SyncPollingWithScene()
    {
        if (CanvasSceneState.Active == CanvasScene.Slideshow) StartPolling();
        else StopPolling();
    }

    /// <summary>问一次联动，把页数与页码对上。</summary>
    /// <summary>
    /// <b>要不要那层能写的面</b>。这是放映模式的两态，与屏幕批注同一套规矩。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用户要的是<b>两个状态</b>，而我先前只做了一个：
    /// <list type="bullet">
    /// <item><b>不书写</b>（选择 / 鼠标）：底下<b>什么都不铺</b> ——
    /// 没有墨迹面、没有背景，只有左下角那个页面控制器与下沉的工具栏。
    /// 这一态存在的理由是<b>不挡住别人的放映</b>：一块全屏可写的透明面压在 PPT 上，
    /// 用户想去点 PPT 的动画按钮就会先被我们吃掉。</item>
    /// <item><b>书写</b>：墨迹面才挂上来，墨迹跟着幻灯片走。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么是"摘面"而不是"关窗口"</b>：关窗口会把左下角那个页面控制器一起带走，
    /// 而那一态里它<b>必须在</b>（用户要看见第几页、还要能翻）。所以只摘墨迹面。
    /// </para>
    /// <para>
    /// 用 <c>DetachFrom</c> 而不是只把 <c>Visibility</c> 设成折叠：<b>折叠的元素不接命中，
    /// 而引擎那块面自己会铺一块透明底占命中范围</b> ——
    /// 只折叠外观而不摘面，症状是"看不见也点不到 PPT"，
    /// 而那与"整块面不见了"在用户眼里是两回事、在代码里却是同一处漏。
    /// </para>
    /// </remarks>
    internal void SetWriting(bool enabled)
    {
        if (_writing == enabled) return;
        _writing = enabled;

        var page = Pages.Count > 0 ? Pages[ActivePageIndex] : null;
        if (page is null) return;

        if (enabled)
        {
            page.Surface.AttachTo(InkHost, 0);
            InkHost.Visibility = Visibility.Visible;
        }
        else
        {
            page.Surface.DetachFrom(InkHost);
            InkHost.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 初值<b>必须是 true</b>，因为基类在 <c>InitializeSharedCanvas</c> 里
    /// <b>已经把第一页的墨迹面挂上去了</b>（<c>_surface.AttachTo(_inkHost, 0)</c>）。
    /// </summary>
    /// <remarks>
    /// 这一条是**用户报"挡住了 PPT 内容"换来的**，而它错得极其隐蔽：
    /// <c>SetWriting</c> 头上有 <c>if (_writing == enabled) return;</c>，
    /// 字段初值写 <c>false</c> 的话，<b>第一次调 <c>SetWriting(false)</c>（鼠标模式的常态）
    /// 自己就短路返回了</b> —— 面一直挂着，而外头看着像"逻辑写了但没生效"。
    /// <para>
    /// 所以初值要照着<b>基类干了什么</b>写，不是照着"我以为干了什么"。
    /// 这类错要能在日志里看出来，所以下面 <see cref="PollOnce"/> 每拍都记一次挂没挂。
    /// </para>
    /// </remarks>
    private bool _writing = true;

    /// <summary>探针用：此刻是不是在书写态（墨迹面挂着）。</summary>
    internal bool IsWriting => _writing;

    /// <summary>工具栏要往这块面上写时走的那个口。<b>不书写时是 null</b> ——
    /// 而那正是"鼠标模式下不该有可写面"这句话在类型上的样子。</summary>
    internal CanvasSurface? ActiveSurfaceForHost =>
        _writing && Pages.Count > 0 ? Pages[ActivePageIndex].Surface : null;

    private void PollOnce()
    {
        if (_link is null) return;

        var report = _link.Probe();
        if (!report.IsLive)
        {
            // **没在放映时把页数收成 0**：留着上一页的页数会让左下角写着"5 / 12"
            // 而屏幕上根本没有放映 —— 那是"我们的读数与事实相反"。
            if (PageCount != 0) TrimPagesTo(0);
            DeckStatusText.Text = report.Detail;
        DeckStatusText.Visibility = Visibility.Collapsed;
        _liveDeckId = report.DeckId;

        // **把"墨迹面挂没挂"写进日志。** 挡住别人的放映是这一块最坏的症状，
        // 而它发生时界面上只有一句话能解释，而那一句话本来是给"没连上"用的。
        var ink = InkHost.Visibility == Visibility.Visible ? "在" : "不在";
        AppLog.Write("放映", $"第 {report.CurrentSlide}/{report.SlideCount} 页，墨迹面{ink}（书写={_writing}）");

            return;
        }

        DeckStatusText.Visibility = Visibility.Collapsed;

        // **页数只听 COM 的。** 不用"合成来源"那份数（那是设置里「预览」用的，12 张假的）。
        if (PageCount != report.SlideCount) ResetPages(report.SlideCount);
        var target = report.CurrentSlide - 1;
        if (target >= 0 && target < PageCount && target != ActivePageIndex) GoToPage(target);
    }

    /// <summary>
    /// 接一份新的缩略图来源：<b>页数按它对账</b>。
    /// </summary>
    internal void UseSource(ISlideThumbnailSource source)
    {
        _source = source;
        // 只有这条路径用"来源"自己的页数 —— 它就是设置里那个「预览」，
        // 页数是那一份合成占位图的（12 张假幻灯片），**不是**真实放映的。
        ResetPages(_source.SlideCount);
        AppLog.Write("放映", $"换了来源：{source.DeckId}，{source.SlideCount} 张（预览用，非真实放映）");
    }

    /// <summary>
    /// 把页数对账到 <paramref name="count"/>，<b>先裁后加</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>页数由参数说了算，不由"来源"说了算</b>。这一条是被用户一句「你进了一个模拟的
    /// 演示模式」逼出来的：原先它读 <c>_source.SlideCount</c>（合成占位图，12 张），
    /// 而 <c>PollOnce</c> 拿 COM 报的真页数（5 张）去比它 ——
    /// <b>两个真相打架</b>，于是真放映时左下角写着「1 / 12」，
    /// 而那 7 张根本不存在。<b>症状不是"数错了"，是"整个模式像是模拟的"</b>。
    /// </para>
    /// <para>
    /// 改法是让<b>谁在管这件事谁报数</b>：连上真放映就报 COM 的，
    /// 只有设置里那个「预览」按钮才用合成的那份。两者不再共用一个字段。
    /// </para>
    /// <para>
    /// <b>加的那一步必须有界</b>：<c>AddPage</c> 内部有"正在拖拽 / 手势中 / 有开着的批"
    /// 三种静默不动的判据，而它不动时页数一页都不涨 —— 无界的 <c>while</c> 于是永远转下去，
    /// <b>整个进程挂死</b>。这是实测挂过一次才写下来的注释。
    /// </para>
    /// </remarks>
    private void ResetPages(int count)
    {
        TrimPagesTo(Math.Min(count, PageCount));

        while (PageCount < count)
        {
            var before = PageCount;
            AddPage(CreatePageCore());
            if (PageCount == before) break;
        }

        RefreshPageBackdrops();
    }

    protected override CanvasPage CreatePageCore()
    {
        var number = PageCount + 1;
        return new SlideShowPage(new CanvasSurface(Dispatcher, assertLoadedSize: false), number);
    }

    /// <summary>
    /// 点那颗钮要做什么：<b>退出放映</b>。
    /// </summary>
    /// <remarks>
    /// <b>它就是基类点名的 <c>OnAddPageRequested</c></b>，而放映里没有"新建一张幻灯片" ——
    /// 幻灯片由宿主给，我们凭空造一张会让页数与真实放映对不上，
    /// 而那个错在页码上看不出来。所以同一颗位置改成退出。
    /// <para>
    /// 切场景由宿主（批注栏）做，窗口自己不改 <see cref="CanvasSceneState"/> ——
    /// "进/出放映"只有一个决策点（<c>ApplyToolbarHosting</c> 也在那儿），
    /// 两处各切一次的后果是场景跳两跳，而中间那一跳用户看得见。
    /// </para>
    /// </remarks>
    protected override void OnAddPageRequested()
    {
        AppLog.Write("放映", "按下退出键");
        ExitRequested?.Invoke(this, EventArgs.Empty);
        AppLog.Write("放映", $"退出键已发出（订阅者 {ExitRequested?.GetInvocationList().Length ?? 0} 个）");
    }

    /// <summary>请求退出放映批注。由宿主（批注栏）接走并切场景。</summary>
    internal event EventHandler? ExitRequested;

    /// <summary>
    /// <b>什么都不铺</b> —— 底下是别人正在放映的幻灯片，铺底就是盖住它。
    /// </summary>
    /// <remarks>
    /// <b>这一条是整块"底下为空"的落点</b>，也是它与图片/展台的分界。
    /// 白板铺底色、图片铺位图、展台每帧铺视频，而这里必须<b>什么都不铺</b>：
    /// 铺一块不透明底的效果是"墨迹更清楚"，看起来像改进，
    /// 实际症状是用户的 PPT 整个看不见了。
    /// <para>
    /// 缩略图那一格也要刷：<b>它要显示"这一页上的墨迹"，而底下是这一页对应的幻灯片</b>，
    /// 所以给一张浅底当纸 —— 那正是白板那边"纯色 + 墨迹"的同一形状。
    /// 真来源接进来之后再换成真的幻灯片画面。
    /// </para>
    /// </remarks>
    protected override void ApplyPageBackdrops()
    {
        // 刻意**不**设 InkHost.Background —— 一旦设了，"底下是透明的"就变成一句谎话。
        foreach (var page in Pages)
        {
            page.Thumbnail.PageBackground = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA);
            page.Thumbnail.Refresh();
        }
    }

    protected override void OnSharedCanvasReady()
    {
        base.OnSharedCanvasReady();

        // 那颗钮是"退出"而不是"新增页面"，所以要把它自己的名字与可用态定下来 ——
        // 漏了的话读屏那一条会念成"新增页面"，而它按下去是退出放映。
        SetThirdButtonEnabled(enabled: true, accessibleName: "退出放映批注");

        // 初始页数同样由"来源"给 —— 连上真实放映之后 <c>PollOnce</c> 会立刻
        // 把它换成 COM 报的真页数，所以这一句只管"还没连上之前别是 0 页"。
        ResetPages(_source.SlideCount);
    }

    /// <summary>探针用：这一块接的是哪一份演示文稿。
    /// <b>连上真放映时给 COM 报的那一份</b>，而不是合成来源那份 ——
    /// 后者永远写着「preview://模拟放映」，而它在真放映时是个<b>谎话</b>。</summary>
    internal string DeckIdForProbe => _liveDeckId.Length > 0 ? _liveDeckId : _source.DeckId;

    /// <summary>最近一次从联动读到的那一份（空串 = 还没连上）。</summary>
    private string _liveDeckId = "";

    /// <summary>探针用：把缩略图来源换成另一份（页数不同），验的是"页数跟着来源走"。</summary>
    internal void ApplySourceForProbe(ISlideThumbnailSource source) => UseSource(source);

    /// <summary>探针用：基类那套缩略图弹层此刻有几格。</summary>
    internal int ThumbnailPopupCardCount => ThumbnailCardCount;

    protected override void OnViewportChangedCore()
    {
        base.OnViewportChangedCore();
        // 放映的视口钉死 1:1，与屏幕批注同一条理由：底下那张幻灯片是按固定比例铺满的，
        // 一旦允许缩放/漫游，墨迹就会与它错位，而用户以为自己在写 PPT。
    }
}
