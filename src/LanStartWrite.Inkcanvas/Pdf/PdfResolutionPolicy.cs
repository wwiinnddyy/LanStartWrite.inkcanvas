using System.Diagnostics;

namespace LanStartWrite.Inkcanvas.Pdf;

/// <summary>
/// 决定"这一页现在该用哪一档分辨率"，以及<b>什么时候该升、什么时候绝不该升</b>。
/// <para>
/// 纯逻辑：不吃 UI、不碰 PDFium、不看时钟以外的东西。<b>所以它能单独被验收</b> ——
/// 而"移动时不发起新光栅化"这条是整个功能流畅与否的分水岭，
/// 它必须是能用十几行断言钉住的东西，而不是"跑起来看着不卡"。
/// </para>
/// <para>
/// <b>三档，不是连续值</b>：低 / 中 / 高。实测（阶段 0 探针，10 页文档，A4）：
/// 72 dpi <b>4 ms</b> / 1 MB，150 dpi <b>9 ms</b> / 8 MB，220 dpi <b>25 ms</b> / 17 MB。
/// 220 dpi 那一档单页就要 25 ms，而滚动时每帧都在动 —— 那一档<b>只能</b>在停稳之后要。
/// </para>
/// </summary>
internal sealed class PdfResolutionPolicy
{
    /// <summary>三档的目标 DPI。<b>按探针实测定的，不是拍的</b>。</summary>
    internal const int LowDpi = 72;

    internal const int MediumDpi = 150;
    internal const int HighDpi = 220;

    /// <summary>
    /// 停稳多久之后才允许升档（毫秒）。
    /// <para>
    /// 150 ms 是<b>手感</b>决定的：短于此用户还在滚，档位上去了又被下一次滚动打掉，
    /// 于是每滚一下都白花一次 25 ms；长于此用户已经停手了却还在看糊的图。
    /// </para>
    /// </summary>
    internal const int SettleMilliseconds = 150;

    /// <summary>
    /// 每帧位移超过这个（DIP）就算"在快速移动"。
    /// <para>
    /// 取 6 DIP 是因为它约等于一行的行高：小于它的移动肉眼看不出滚动，
    /// 而那时候升档只会白花钱。
    /// </para>
    /// </summary>
    internal const double FastMoveDip = 6;

    private readonly Stopwatch _clock = new();
    private double _lastMoveX;
    private double _lastMoveY;
    private bool _hasLastMove;
    private TimeSpan _lastMoveAt;

    /// <summary>
    /// 时钟。<b>可注入</b>，因为"停稳 150 ms"这条规则必须能用断言钉住 ——
    /// 靠真实时间测它，写出来的是"睡 150 ms 再看"，那种测试要么慢要么 flaky，
    /// 于是这条规则实际上<b>永远没被测过</b>。注入之后它就是一行纯计算。
    /// </summary>
    internal Func<TimeSpan> Now { get; set; } = null!;

    private TimeSpan Elapsed => Now?.Invoke() ?? _clock.Elapsed;

    /// <summary>当前<b>已发布</b>的那一档。降档是立刻的，升档要等停稳。</summary>
    internal ResolutionTier Published { get; private set; } = ResolutionTier.Medium;

    /// <summary>供验收读：上一次 <see cref="NoteViewportMoved"/> 的判定。</summary>
    internal bool IsMovingFast { get; private set; }

    /// <summary>视口动了一格。<b>必须在 UI 线程调</b>（它读时钟）。</summary>
    internal void NoteViewportMoved(double screenX, double screenY)
    {
        if (_hasLastMove)
        {
            var dx = screenX - _lastMoveX;
            var dy = screenY - _lastMoveY;
            IsMovingFast = Math.Abs(dx) > FastMoveDip || Math.Abs(dy) > FastMoveDip;
        }

        _lastMoveX = screenX;
        _lastMoveY = screenY;
        _hasLastMove = true;
        _lastMoveAt = Elapsed;
    }

    /// <summary>
    /// 算"现在该显示哪一档"。<b>不发布任何东西</b>，只回答该要哪一档。
    /// <para>
    /// 规则，按优先级：
    /// <list type="number">
    /// <item><b>快速移动中</b> → <see cref="ResolutionTier.Low"/>。这是流畅的关键：
    /// 移动期间<b>一个像素都不该新栅格化</b>，宁可糊。</item>
    /// <item><b>停稳超过 <see cref="SettleMilliseconds"/></b> → 按缩放给该给的那一档。</item>
    /// <item>其余（刚停下、还没到沉降时间）→ 保持 <see cref="Published"/> 不动。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal ResolutionTier DesiredTier(double zoom)
    {
        if (IsMovingFast) return ResolutionTier.Low;

        // 还没动过 = 已经静止了很久。刚打开文档时正是这样，而如果把"距上次移动"
        // 从零算起，第一帧会被判成"才刚停"，于是<b>用户看到的永远是中档</b>，
        // 得先随便动一下鼠标才升得上去。
        if (!_hasLastMove) return TierForZoom(zoom);

        var sinceMove = Elapsed - _lastMoveAt;
        if (sinceMove < TimeSpan.FromMilliseconds(SettleMilliseconds)) return Published;

        return TierForZoom(zoom);
    }

    private static ResolutionTier TierForZoom(double zoom) => zoom switch
    {
        < 0.75 => ResolutionTier.Low,
        < 1.6 => ResolutionTier.Medium,
        _ => ResolutionTier.High,
    };

    /// <summary>把已发布的那一档记下来（真的画上去了才调，否则就成了"以为升过了"）。</summary>
    internal void Publish(ResolutionTier tier) => Published = tier;

    /// <summary>打开新文档时复位。</summary>
    internal void Reset()
    {
        Published = ResolutionTier.Medium;
        IsMovingFast = false;
        _hasLastMove = false;
        _clock.Restart();
        _lastMoveAt = TimeSpan.Zero;
    }

    /// <summary>这一档的目标 DPI。</summary>
    internal static int DpiOf(ResolutionTier tier) => tier switch
    {
        ResolutionTier.Low => LowDpi,
        ResolutionTier.High => HighDpi,
        _ => MediumDpi,
    };

    /// <summary>一格分辨率。三档而不是连续值：档位多了会让"该换档"这件事频繁发生。</summary>
    internal enum ResolutionTier
    {
        Low,
        Medium,
        High,
    }
}
