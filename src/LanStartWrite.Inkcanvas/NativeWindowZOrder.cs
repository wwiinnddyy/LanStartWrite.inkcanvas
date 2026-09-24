using System.Runtime.InteropServices;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 排 Z 序用的那几个 Win32 调用。<b>本文件只做互操作，不含任何层级策略</b> ——
/// "谁该在谁上面"在 <see cref="WindowLayerManager"/> 里，"怎么放到那上面"在这里，两者分开读。
/// <para>
/// <b>为什么非要有它</b>：Jalium 的 <c>Window.Topmost</c> 只能表达"在不在置顶带里"，
/// 而<b>同一个置顶带内部的先后没有公开 API</b> —— 带内顺序要么靠激活（把窗口顶到带首），
/// 要么靠 <c>SetWindowPos</c>。本应用要的恰恰是带内的固定顺序
/// （工具栏在画布之上、设置在画布之上），而且要它稳定、不是"最近被点过的在上面"，
/// 所以必须自己持有 <c>SetWindowPos</c>。
/// </para>
/// <para>
/// <b>排 Z 序一律带 <c>SWP_NOACTIVATE</c></b>：排序不是"把谁叫到前台"。
/// 少了它，每次维护层级都会把键盘焦点从当前控件上夺走 ——
/// 表现是"在设置页拖滑杆拖到一半，焦点跳走了"，而且这种错很难往"窗口管理"上想。
/// </para>
/// <para>
/// 平台：只发行 <c>net10.0-windows</c>，所以直接 DllImport user32，不做跨平台兜底。
/// 非 Windows 上这些入口根本不会被调到（<see cref="WindowLayerManager"/> 在拿不到句柄时直接跳过）。
/// </para>
/// <para>
/// <b>为什么是 <c>DllImport</c> 而不是 <c>LibraryImport</c></b>：后者要求整个工程开
/// <c>AllowUnsafeBlocks</c>（SYSLIB1062），而这里六个入口的参数全是 <c>IntPtr</c> / <c>int</c> / <c>bool</c>，
/// 都是可直接传递的位模式，源生成器给不出更快的东西。为六个调用把整个项目的不安全代码闸门打开，
/// 换不来任何收益 —— 这一条是有意选的，不是忘了升级。
/// </para>
/// </summary>
internal static class NativeWindowZOrder
{
    // ---- hWndInsertAfter 的四个哨兵值（Win32 头文件里的固定常量，不是句柄）----

    /// <summary>放到 Z 序最前。对置顶窗口而言是"置顶带之首"。</summary>
    internal static readonly IntPtr Top = IntPtr.Zero;

    /// <summary>放到 Z 序最后。</summary>
    internal static readonly IntPtr Bottom = new(1);

    /// <summary>进入置顶带，并落到带首。</summary>
    internal static readonly IntPtr Topmost = new(-1);

    /// <summary>离开置顶带，落到非置顶带之首（位于所有置顶窗口之后）。</summary>
    internal static readonly IntPtr NotTopmost = new(-2);

    // ---- SetWindowPos 的 flags ----

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>我们只用它来排 Z 序，因此永远带着这三个 flag。</summary>
    private const uint ZOrderOnly = SwpNoSize | SwpNoMove | SwpNoActivate;

    // ---- GetWindow 的 uCmd ----

    private const uint GwHwndNext = 2; // Z 序里更低的那一个
    private const uint GwHwndPrev = 3; // Z 序里更高的那一个

    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;

    /// <summary>
    /// <c>WS_EX_TRANSPARENT</c>：命中测试跳过这个窗口。
    /// <b>注意它单独用只对"同一线程的兄弟窗口"生效</b> —— 要点到别的应用上还得配
    /// <c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes</c>（见 <see cref="SetClickThrough"/>）。
    /// </summary>
    private const long WsExTransparent = 0x00000020L;

    /// <summary><c>WS_EX_LAYERED</c>：分层窗口。命中测试对<b>别的进程</b>生效的前提。</summary>
    private const long WsExLayered = 0x00080000L;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    /// <summary>
    /// 平台收口。<b>Linux 打包（AppImage）之后这份互操作不会被调到</b>：
    /// 每个公开入口都先看这一位，非 Windows 一律返回"没生效 / 不知道"，
    /// 于是层级的模型照常运转、只是没有原生 Z 序可排 —— 那是 Linux 端目前的现实，
    /// 与其让一次 DllNotFoundException 把应用打崩，不如把"没有"表达成返回值。
    /// </summary>
    private static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// 把 <paramref name="window"/> 插到 <paramref name="insertAfter"/> 的<b>下面</b>
    /// （Win32 的说法是"紧跟在它之后"，而"之前/之后"在本文件里一律指 Z 序方向）。
    /// <para>
    /// 传 <see cref="Top"/> / <see cref="Topmost"/> / <see cref="NotTopmost"/> / <see cref="Bottom"/>
    /// 就是那几个哨兵语义；传真实句柄则是"贴在它下面"，这也是本应用排<b>带内顺序</b>的唯一手段。
    /// </para>
    /// <para>返回 false 只说明这一句没生效（句柄失效、被别的进程拦住等），调用方按"下一拍再试"处理即可。</para>
    /// </summary>
    internal static bool PlaceAfter(IntPtr window, IntPtr insertAfter)
    {
        if (!IsWindows || window == IntPtr.Zero) return false;
        return SetWindowPos(window, insertAfter, 0, 0, 0, 0, ZOrderOnly);
    }

    /// <summary>Z 序里紧挨在它<b>上方</b>的那个窗口；没有则返回 <see cref="IntPtr.Zero"/>。</summary>
    internal static IntPtr NeighbourAbove(IntPtr window) => !IsWindows ? IntPtr.Zero : GetWindow(window, GwHwndPrev);

    /// <summary>Z 序里紧挨在它<b>下方</b>的那个窗口；没有则返回 <see cref="IntPtr.Zero"/>。</summary>
    internal static IntPtr NeighbourBelow(IntPtr window) => !IsWindows ? IntPtr.Zero : GetWindow(window, GwHwndNext);

    /// <summary>整个桌面 Z 序最顶端的那个顶层窗口。遍历 Z 序从这里起步。</summary>
    internal static IntPtr DesktopTop() => !IsWindows ? IntPtr.Zero : GetTopWindow(IntPtr.Zero);

    /// <summary>
    /// 这个窗口是否带着 <c>WS_EX_TOPMOST</c>。<b>这才是"压得住其他应用"的真正依据</b> ——
    /// 置顶是外壳（DWM）维持的：凡是普通窗口，永远盖不到带这个样式的窗口上面。
    /// 层级的验收因此不能只看"我调过一句 Topmost"，要回读这个位。
    /// </summary>
    internal static bool HasTopmostStyle(IntPtr window) => IsWindows && (ReadExStyle(window) & WsExTopmost) != 0;

    /// <summary>
    /// 这个窗口是否带着 <c>WS_EX_TRANSPARENT</c>（即"穿透模式"真的生效了）。
    /// <para>与置顶一样，<b>这也要回读而不是回读我们设过的属性</b>：样式位是外壳认的东西，
    /// 我们设过一句不等于它还在（别的进程、某些自动化工具都能改）。</para>
    /// </summary>
    internal static bool HasClickThroughStyle(IntPtr window) => IsWindows && (ReadExStyle(window) & WsExTransparent) != 0;

    /// <summary>
    /// 开 / 关穿透：命中测试跳过这个窗口，鼠标与触摸直接落到它下面的窗口上。
    /// <para>
    /// <b>这是一套配方，不是一位</b>（用户报过"穿透开着，点下去还是在写字"）：
    /// <list type="number">
    /// <item><c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes(不透明)</c> ——
    /// 分层是命中测试对<b>别的进程</b>生效的前提，而"没设过分层属性的分层窗口不显示"，
    /// 所以补 LAYERED 的同时必须把它设成完全不透明（改变的只是命中，不是显示）；</item>
    /// <item><c>WS_EX_TRANSPARENT</c> —— 同线程那一半；</item>
    /// <item><c>WM_NCHITTEST</c> 回 <c>HTTRANSPARENT</c> 的钩子（见画布窗口）—— 最后一道保险。</item>
    /// </list>
    /// 关掉时只清 <c>WS_EX_TRANSPARENT</c>：LAYERED 留着（它不影响显示），
    /// 反复开关不至于让窗口在"显示 / 不显示"之间跳。
    /// </para>
    /// <para>返回 false 表示这一句没生效（句柄失效、平台不支持等），调用方按"下一拍再试"处理。</para>
    /// </summary>
    internal static bool SetClickThrough(IntPtr window, bool enabled)
    {
        if (!IsWindows || window == IntPtr.Zero) return false;

        var current = ReadExStyle(window);
        var updated = enabled
            ? current | WsExTransparent | WsExLayered
            : current & ~WsExTransparent;
        if (updated != current && !WriteExStyle(window, updated)) return false;

        // 分层窗口必须设置过 attributes 才会被画出来（没设过的根本不显示）。
        // alpha 给 255：画布本来就该 1:1 显示，这里改变的只是命中测试，不是透明度。
        return !enabled
            || SetLayeredWindowAttributes(window, 0, 255, LwaAlpha)
            || Marshal.GetLastWin32Error() == 0;
    }

    /// <summary><c>WM_NCHITTEST</c> 的"别把鼠标给我"返回值（Win32 里的 <c>HTTRANSPARENT</c>）。</summary>
    internal static readonly IntPtr HitTestTransparent = new(-1);

    private const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    /// <summary>
    /// 问操作系统："这个屏幕坐标上的鼠标事件会交给哪个窗口？"
    /// <para>
    /// 穿透的验收只能这么问 —— 我们设过什么样式、框架记着什么状态都说明不了问题，
    /// 命中测试是外壳做的。<b>穿透真的生效</b>等价于"画布上那一点命中到的不是画布"。
    /// </para>
    /// </summary>
    internal static IntPtr WindowHitTest(int x, int y) => !IsWindows ? IntPtr.Zero : WindowFromPoint(new PointStruct { X = x, Y = y });

    /// <summary>窗口在屏幕上的物理矩形（左 / 上 / 右 / 下）。句柄无效或取不到时返回 <c>null</c>。</summary>
    internal static (int Left, int Top, int Right, int Bottom)? WindowRect(IntPtr window)
    {
        if (!IsWindows || window == IntPtr.Zero) return null;
        if (!GetWindowRect(window, out var rect)) return null;
        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr WindowFromPoint(PointStruct point);

    private static long ReadExStyle(IntPtr window)
    {
        if (window == IntPtr.Zero) return 0;

        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, GwlExStyle).ToInt64()
            : GetWindowLong32(window, GwlExStyle);
    }

    private static bool WriteExStyle(IntPtr window, long exStyle)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(window, GwlExStyle, new IntPtr(exStyle)) != IntPtr.Zero
                || Marshal.GetLastWin32Error() == 0;

        return SetWindowLong32(window, GwlExStyle, (int)exStyle) != 0 || Marshal.GetLastWin32Error() == 0;
    }
}
