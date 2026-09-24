using Jalium.UI;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// <b>窗口层级系统</b>：一处在应用里说清"哪个窗口永远在哪个窗口上面"，然后由它去执行。
/// <para>
/// <b>它解决的问题</b>：在这之前，层级是靠各窗口自己喊 <c>Topmost = true/false</c> 维持的 ——
/// 显示画布时顺手把工具栏也顶一下、打开设置时先把工具栏降下来、菜单跟着工具栏的 Topmost 抄一份。
/// 三处调用点各写一遍，谁也说不准"现在到底谁在上面"；加第四个窗口时只能靠试。
/// 这里把它换成一个模型：<b>每个窗口登记一个层级，层级从低到高是
/// 画布 &lt; 工具栏 &lt; 二级菜单 &lt; 对话框</b>；剩下的事由本类算。
/// </para>
/// <para>
/// <b>两条不变量</b>（<see cref="Verify"/> 可回读真实 Z 序来验）：
/// <list type="number">
/// <item>同一时刻可见的两个窗口，层级高的在层级低的之上；</item>
/// <item>凡"该压过其他应用"的窗口都带 <c>WS_EX_TOPMOST</c>，凡不该带的都不带。</item>
/// </list>
/// 第 2 条才是"画布不会被别的应用盖住"的真正依据 —— 置顶是外壳维持的，
/// 普通窗口永远盖不到置顶窗口上面。
/// </para>
/// <para>
/// <b>为什么写死"画布可见就置顶"</b>：一个吃满全屏的透明画布如果不置顶，
/// 它就是"被任何窗口压住、又压住桌面"的中间层，谁也说不清它该在哪。
/// 把它钉在置顶带上，"画布必须压过其他应用"这条要求才有确定的落点。
/// </para>
/// <para>
/// <b>钉住是向上继承的</b>：下面有一个置顶窗口，上面那些就<b>必须</b>也置顶 ——
/// 否则非置顶的窗口永远在所有置顶窗口之下，层级关系就自相矛盾了。
/// 因此调用方只需要说"画布可见 / 工具栏要不要始终置顶"，
/// 菜单与对话框要不要置顶是从下面推出来的，不用各记一份。
/// </para>
/// <para>
/// <b>带是有条件的，序是无条件的</b>：画布层只要可见就永远置顶（"批注时盖住其他应用"是产品要求）；
/// 而工具栏那条"始终置顶"偏好只在<b>没有对话框</b>时算数 —— 对话框在场意味着用户暂时不在批注状态，
/// 此时整个应用退出置顶带，设置窗口才能像普通窗口一样被压到别的应用后面。
/// 注意这只影响"在不在置顶带里"，<b>不影响先后</b>：对话框 &gt; 菜单 &gt; 工具栏 &gt; 画布
/// 这一条两种情况下都照排，靠的是 <c>SetWindowPos</c>，与置顶带无关。
/// </para>
/// <para>
/// <b>线程</b>：与偏好设置、笔锋一致，只在 UI 线程上访问（窗口事件与 <see cref="DispatcherTimer"/>
/// 都在 UI 线程），因此没有锁。
/// </para>
/// </summary>
internal static class WindowLayerManager
{
    private sealed class Entry
    {
        internal Window Window = null!;
        internal WindowLayer Layer;
        internal string Name = string.Empty;

        /// <summary>显式钉住：来自工具状态或用户偏好（"始终置顶工具栏"）。</summary>
        internal bool Pinned;

        /// <summary>本层内的先后，值越大越靠上。窗口被激活时抬到本层之首。</summary>
        internal int Order;

        /// <summary>本次结算时的句柄快照（0 = 原生窗口还没建出来）。</summary>
        internal IntPtr Handle;

        /// <summary>本次结算时算出来的置顶态（显式钉住 + 从下面继承上来的）。</summary>
        internal bool EffectivePinned;

        /// <summary>
        /// 在不在屏上。<b>只由事件维护</b>（<c>Shown</c> 置真、<c>Hiding</c> 置假），不看 <c>Window.Visibility</c> ——
        /// 后者在"从没 Show 过"的窗口上是个不可靠的初值，而这里判错的后果很实在：
        /// 把一个没显形的窗口拿去排 Z 序，或者把一个显形的窗口漏掉。
        /// 登记那一刻用一个保守的初值（有句柄且可见才算可见），之后一律听事件。
        /// </summary>
        internal bool IsShown;

        internal void Refresh() => Handle = Window.Handle;
    }

    /// <summary>登记顺序表。<b>顺序即同层内的初始先后</b>。</summary>
    private static readonly List<Entry> Entries = [];

    /// <summary>本拍可见的窗口，按"层升序 + 层内先后"排好。复用同一个列表，免得每次结算都分配。</summary>
    private static readonly List<Entry> Visible = [];

    private static int _nextOrder;

    private static TimeSpan _autoRepairInterval = TimeSpan.FromMilliseconds(1500);
    private static DispatcherTimer? _autoRepair;

    /// <summary>最近的违规项（空 = 层级成立）。给日志与探针看。</summary>
    internal static IReadOnlyList<string> LastViolations { get; private set; } = [];

    /// <summary>回读 Z 序的次数。<see cref="Verify"/> 被探针调也算在内。</summary>
    internal static int VerifyCount { get; private set; }

    /// <summary>周期性自检真正跑过的拍数。<b>它大于 0 才说明自愈那道网是活的</b>（不是只写了没接上）。</summary>
    internal static int AutoRepairChecks { get; private set; }

    /// <summary>自检发现飘了、真的动手重排的次数。</summary>
    internal static int RepairCount { get; private set; }

    internal static int RegisteredCount => Entries.Count;

    /// <summary>
    /// 自愈间隔。零或负值表示关掉周期性自检（只靠窗口事件结算）。
    /// <para>
    /// 之所以要有这一道：层级的破坏源不只是本应用 —— 外壳重排置顶窗口、目标机上的其它常驻工具、
    /// 锁屏唤醒之类，都能把顺序挪走。事件驱动盯不住这些，而"每拍都无脑 SetWindowPos"又太吵，
    /// 所以这一道是<b>先回读、发现真的偏了才动手</b>，正常时只是走一趟 Z 序而已。
    /// </para>
    /// </summary>
    internal static TimeSpan AutoRepairInterval
    {
        get => _autoRepairInterval;
        set
        {
            _autoRepairInterval = value;
            EnsureTimer();
            SyncTimerState();
        }
    }

    /// <summary>
    /// 登记一个窗口。在窗口自己的构造函数里调即可 —— 它还没 Show、句柄还是 0，
    /// 本类会把"没句柄"当成"不可见"，等 <c>Shown</c> 那一拍再排。
    /// </summary>
    /// <param name="window">要纳管的窗口。</param>
    /// <param name="layer">它属于哪一层。见 <see cref="WindowLayer"/> 的说明。</param>
    /// <param name="name">给日志与断言看的名字（不是显示名，进不了界面）。</param>
    internal static void Register(Window window, WindowLayer layer, string name)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        var existing = Find(window);
        if (existing is not null)
        {
            existing.Layer = layer;
            existing.Name = name;
            Reconcile();
            return;
        }

        var entry = new Entry
        {
            Window = window,
            Layer = layer,
            Name = name,
            Order = ++_nextOrder,
            Handle = window.Handle,
        };
        entry.IsShown = entry.Handle != IntPtr.Zero && window.Visibility == Visibility.Visible;
        Entries.Add(entry);

        // 窗口自己的生命周期就是结算时机：显形、隐藏、被激活、关闭。
        // 调用方因此不需要记得"改完还要叫一声" —— 只有改"钉住"那条策略要显式说（SetPinned）。
        window.Shown += (_, _) => { entry.IsShown = true; Reconcile(); };
        window.Hiding += (_, _) => { entry.IsShown = false; Reconcile(); };
        window.Activated += (_, _) => OnActivated(entry);
        window.Closed += (_, _) => Unregister(window);

        EnsureTimer();
        Reconcile();
    }

    /// <summary>注销。窗口 <c>Closed</c> 时会自动走这一趟，正常不需要手动调。</summary>
    internal static void Unregister(Window window)
    {
        var entry = Find(window);
        if (entry is null) return;
        Entries.Remove(entry);
        Reconcile();
    }

    /// <summary>
    /// 改"要不要压过其他应用"这条策略。
    /// <para>
    /// <b>不要用它表达层级</b> —— 层级是登记时定死的；本方法是给"同一个窗口在不同状态下要不要置顶"用的，
    /// 目前只有一处：工具栏那个"始终置顶工具栏"偏好（鼠标模式下也保持置顶）。
    /// 画布不用调它：画布层只要可见就置顶，见 <see cref="MustBePinned"/>。
    /// </para>
    /// </summary>
    internal static void SetPinned(Window window, bool pinned)
    {
        var entry = Find(window);
        if (entry is null || entry.Pinned == pinned) return;
        entry.Pinned = pinned;
        Reconcile();
    }

    /// <summary>
    /// 按当前登记与可见性排一次 Z 序。<b>幂等</b>：状态没变时重复调不会改变画面
    /// （顺序本来就是对的时候，那几句 <c>SetWindowPos</c> 只是把它写在同一个位置上）。
    /// </summary>
    internal static void Reconcile()
    {
        if (Entries.Count == 0) return;

        ComputeStack();
        ApplyStack();
    }

    /// <summary>
    /// 回读真实 Z 序，列出所有违反层级的地方（空表示成立）。
    /// <para>
    /// 它是"这套系统真的成立"的验收口，也是自愈那一拍的判据 —— 不是断言我们设过什么属性，
    /// 而是问操作系统"现在到底谁在上面"。这样即使有外力挪动了顺序，也能被发现。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Verify()
    {
        VerifyCount++;
        var problems = new List<string>();
        if (Entries.Count == 0) return problems;

        ComputeStack();

        foreach (var entry in Visible)
        {
            // 带：直接回读 WS_EX_TOPMOST，而不是回读我们设过的属性。
            var topmost = NativeWindowZOrder.HasTopmostStyle(entry.Handle);
            if (entry.EffectivePinned && !topmost)
                problems.Add($"{entry.Name} 应当压过其他应用，但没带 WS_EX_TOPMOST");
            else if (!entry.EffectivePinned && topmost)
                problems.Add($"{entry.Name} 不该压过其他应用，却带着 WS_EX_TOPMOST");
        }

        var ranks = ReadZOrderRanks();
        for (var lower = 0; lower < Visible.Count; lower++)
        {
            for (var higher = lower + 1; higher < Visible.Count; higher++)
            {
                var low = Visible[lower];
                var high = Visible[higher];
                if (!ranks.TryGetValue(low.Handle, out var lowRank)) continue;
                if (!ranks.TryGetValue(high.Handle, out var highRank)) continue;

                // 排名从小到大＝从最上面往下面数，所以"更高的那个"排名必须更小。
                if (highRank >= lowRank)
                    problems.Add($"{high.Name} 在 {low.Name} 之下，与层级要求相反");
            }
        }

        LastViolations = problems;
        return problems;
    }

    /// <summary>
    /// 回读某个已登记窗口当前是否"压得住其他应用"（带着 <c>WS_EX_TOPMOST</c>）。
    /// 与 <see cref="SetPinned"/> 的区别是方向：那是"我希望它怎样"，这是"它现在到底怎样"。
    /// </summary>
    internal static bool IsAboveOtherApps(Window window)
    {
        var entry = Find(window);
        if (entry is null) return false;

        entry.Refresh();
        return NativeWindowZOrder.HasTopmostStyle(entry.Handle);
    }

    /// <summary>当前层级栈的自述（从最上面那个往下写），给日志与断言看。</summary>
    internal static string Describe()
    {
        ComputeStack();
        if (Visible.Count == 0) return "(无可见窗口)";

        var parts = new List<string>(Visible.Count);
        for (var i = Visible.Count - 1; i >= 0; i--)
        {
            var entry = Visible[i];
            parts.Add($"{entry.Name}[{entry.Layer}{(entry.EffectivePinned ? ",置顶" : string.Empty)}]");
        }

        return string.Join(" > ", parts);
    }

    /// <summary>
    /// 本层是否"只要可见就必须压过其他应用"。
    /// <para>
    /// 目前只有画布层是这样。把它写成层自带的性质而不是某个窗口的开关，
    /// 是因为这是产品要求（批注时要盖住别的应用），不是某个调用点的选择 ——
    /// 放在窗口上，迟早会有一个新窗口忘了打开它。
    /// </para>
    /// </summary>
    private static bool MustBePinned(Entry entry) => entry.Layer == WindowLayer.Canvas;

    /// <summary>当前是否有对话框在场的（可见）。见 <see cref="ComputeStack"/> 里"带与序分开"的那段。</summary>
    internal static bool DialogPresent { get; private set; }

    private static void OnActivated(Entry entry)
    {
        // 被激活的窗口抬到<b>本层之首</b>，而不是整个栈之首：点设置窗口不该让它越过画布的层级，
        // 层级是不可越的，层内先后才是"最近点过的在前"。
        entry.Order = ++_nextOrder;
        Reconcile();
    }

    private static Entry? Find(Window window)
    {
        foreach (var entry in Entries)
        {
            if (ReferenceEquals(entry.Window, window)) return entry;
        }

        return null;
    }

    /// <summary>算出这一拍的可见栈与置顶态。</summary>
    private static void ComputeStack()
    {
        Visible.Clear();
        foreach (var entry in Entries)
        {
            entry.Refresh();
            if (entry.IsShown) Visible.Add(entry);
        }

        Visible.Sort(static (a, b) =>
            a.Layer != b.Layer
                ? ((int)a.Layer).CompareTo((int)b.Layer)
                : a.Order.CompareTo(b.Order));

        DialogPresent = Visible.Exists(static entry => entry.Layer == WindowLayer.Dialog);

        // 置顶自下而上继承：下面有置顶的，上面就必须也置顶（见类注释）。
        // 画布层无条件参与置顶；工具栏那条偏好在没有对话框时才算数 ——
        // "压过其他应用"承诺的是批注这件事，不是这个应用永远在最上面。
        var pinned = false;
        foreach (var entry in Visible)
        {
            pinned |= MustBePinned(entry) || (!DialogPresent && entry.Pinned);
            entry.EffectivePinned = pinned;
        }
    }

    /// <summary>把算好的栈落到原生 Z 序上。</summary>
    private static void ApplyStack()
    {
        // 从最上面那个往下排：每个窗口贴到上一个（更高）的下面。
        // 先落最高那个 —— 它没有"上一个"，所以用带哨兵：置顶的用 HWND_TOPMOST（带首），
        // 不置顶的用 HWND_TOP（非置顶带之首）。两个都是从零开始确定位置的写法。
        var higher = IntPtr.Zero;
        for (var i = Visible.Count - 1; i >= 0; i--)
        {
            var entry = Visible[i];

            // 带：走框架自己的属性，它同时会把 WS_EX_TOPMOST 设对。只在确实不同时才写。
            if (entry.Window.Topmost != entry.EffectivePinned)
                entry.Window.Topmost = entry.EffectivePinned;

            if (entry.Handle == IntPtr.Zero) continue; // 句柄还没建出来，等 Shown 那一拍

            var insertAfter = higher != IntPtr.Zero
                ? higher
                : entry.EffectivePinned
                    ? NativeWindowZOrder.Topmost
                    : NativeWindowZOrder.Top;

            NativeWindowZOrder.PlaceAfter(entry.Handle, insertAfter);
            higher = entry.Handle;
        }
    }

    /// <summary>
    /// 某个已登记窗口当前在<b>整个桌面</b> Z 序里排第几（0 = 最上面）。没登记、没句柄、或不在链子上返回 -1。
    /// <para>
    /// 给日志与断言看：它问的是操作系统"现在谁在上面"，而不是问我们设过什么属性 ——
    /// 层级这种事的验收只能这么问，否则验的是自己的记忆。
    /// </para>
    /// </summary>
    internal static int RankOf(Window window)
    {
        var entry = Find(window);
        if (entry is null) return -1;

        entry.Refresh();
        if (entry.Handle == IntPtr.Zero) return -1;

        var ranks = ReadZOrderRanks([entry.Handle]);
        return ranks.TryGetValue(entry.Handle, out var rank) ? rank : -1;
    }

    /// <summary>
    /// 从桌面 Z 序的最顶端往下走，记下这几个句柄排第几（0 = 整个桌面最上面）。
    /// 找齐了就不再往下走 —— 我们的窗口都在置顶带上，通常几步就收工。
    /// </summary>
    private static Dictionary<IntPtr, int> ReadZOrderRanks(IReadOnlyList<IntPtr> handles)
    {
        var ranks = new Dictionary<IntPtr, int>(handles.Count);
        if (handles.Count == 0) return ranks;

        var window = NativeWindowZOrder.DesktopTop();
        var rank = 0;

        // 上界只是防链子出环：正常桌面是有限条链表，走到 0 就停。
        while (window != IntPtr.Zero && rank < 10000)
        {
            for (var i = 0; i < handles.Count; i++)
            {
                if (handles[i] != window || ranks.ContainsKey(window)) continue;
                ranks[window] = rank;
                if (ranks.Count == handles.Count) return ranks;
            }

            window = NativeWindowZOrder.NeighbourBelow(window);
            rank++;
        }

        return ranks;
    }

    private static Dictionary<IntPtr, int> ReadZOrderRanks()
    {
        var wanted = new List<IntPtr>(Visible.Count);
        foreach (var entry in Visible)
        {
            if (entry.Handle != IntPtr.Zero) wanted.Add(entry.Handle);
        }

        return ReadZOrderRanks(wanted);
    }

    private static void EnsureTimer()
    {
        if (_autoRepair is not null) return;

        _autoRepair = new DispatcherTimer { Interval = _autoRepairInterval };
        _autoRepair.Tick += (_, _) => RepairIfDrifted();
        SyncTimerState();
    }

    private static void SyncTimerState()
    {
        if (_autoRepair is null) return;
        if (_autoRepairInterval > TimeSpan.Zero)
        {
            _autoRepair.Interval = _autoRepairInterval;
            _autoRepair.Start();
        }
        else
        {
            _autoRepair.Stop();
        }
    }

    private static void RepairIfDrifted()
    {
        AutoRepairChecks++;
        LastViolations = Verify();
        if (LastViolations.Count == 0) return;

        RepairCount++;
        Reconcile();
    }
}
