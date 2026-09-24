using System.Collections.Generic;
using Jalium.UI;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 双指手势的<b>算术半边</b>：把"若干根手指各自在哪"折成"这一拍画面平移多少、绕哪个点缩放几倍"。
/// <para>
/// 单独一个类、且不碰任何 UI 的原因是可测：捏合的唯一硬判据是
/// <b>两指中点之下那个世界点在缩放前后保持不动</b>（地图与白板都是这一条），
/// 而这条能纯算出来 —— 不需要真手指，也不需要真屏幕。
/// </para>
/// <para>
/// 框架自带的 Manipulation 只到"单指平移"（<c>Window.CreateManipulationDelta</c> 把 Scale 硬编成 (1,1)），
/// 所以捏合必须在这里算。落点与缩放最终交给引擎的 <c>InkViewport</c>
/// （宿主不许自己给控件加变换 —— 视口矩阵是引擎的事）。
/// </para>
/// </summary>
internal sealed class TouchGestureTracker
{
    /// <summary>两指相距小于这个数就不认缩放：除出来的因子会飞，而手指本来也捏不出角度。</summary>
    private const double MinDistance = 12;

    private readonly Dictionary<int, Point> _contacts = [];
    private Point _lastMid;
    private double _lastDistance;

    /// <summary>抬到只剩一根时，那根要"停住"到它自己抬起 —— 不然它会突然变成一次单指选择。</summary>
    private readonly HashSet<int> _parked = [];

    /// <summary>是不是正处在双指手势里。</summary>
    internal bool IsActive { get; private set; }

    /// <summary>当前落在板上的手指数（含被"停住"的那些）。</summary>
    internal int ContactCount => _contacts.Count;

    /// <summary>
    /// 一根手指落下。<b>返回 true 表示这一拍起进入（或正处于）双指手势</b>，
    /// 调用方因此要把已经在跑的单指选择作废。
    /// </summary>
    internal bool Down(int id, Point position)
    {
        _contacts[id] = position;
        if (_contacts.Count >= 2)
        {
            Begin();
            return true;
        }

        return IsActive;
    }

    /// <summary>
    /// 手指移动。<b>返回 true 表示这一拍被手势吃掉</b>（调用方不要再拿它去做选择）。
    /// 被"停住"的那根返回 true 但位移是零 —— 它此刻不该再有反应，但也别把事件漏给选择。
    /// </summary>
    internal bool Move(int id, Point position, out Point pan, out double zoomFactor, out Point anchor)
    {
        pan = default;
        zoomFactor = 1;
        anchor = default;
        if (!_contacts.ContainsKey(id)) return false;

        _contacts[id] = position;
        if (!IsActive) return false;

        if (_contacts.Count < 2)
        {
            // 2 → 1：剩下那根停住，直到它抬起。这里"什么都不做"是对的，
            // 而返回 true 是必要的 —— 漏回去它就会在画面中央拖出一个莫名其妙的框选。
            return true;
        }

        var (mid, distance) = MidpointAndDistance();
        pan = new Point(mid.X - _lastMid.X, mid.Y - _lastMid.Y);
        zoomFactor = _lastDistance > MinDistance && distance > MinDistance
            ? distance / _lastDistance
            : 1;
        anchor = mid;
        _lastMid = mid;
        _lastDistance = distance;
        return true;
    }

    /// <summary>一根手指抬起。<b>返回 true 表示手势还在继续</b>（比如还有两根在下头）。</summary>
    internal bool Up(int id)
    {
        _contacts.Remove(id);
        _parked.Remove(id);

        if (_contacts.Count >= 2)
        {
            var (mid, distance) = MidpointAndDistance();
            _lastMid = mid;
            _lastDistance = distance;
            return true;
        }

        if (_contacts.Count == 1)
        {
            // 还剩一根：记下它，Move 里按"停住"处理。
            IsActive = false;
            foreach (var remaining in _contacts.Keys) _parked.Add(remaining);
            return false;
        }

        Reset();
        return false;
    }

    /// <summary>整套状态作废（指针取消 / 换档 / 关白板）。</summary>
    internal void Reset()
    {
        _contacts.Clear();
        _parked.Clear();
        IsActive = false;
        _lastDistance = 0;
    }

    /// <summary>被"停住"的那根手指（2 → 1 之后剩下的一根）。</summary>
    internal bool IsParked(int id) => _parked.Contains(id);

    private void Begin()
    {
        var (mid, distance) = MidpointAndDistance();
        if (!IsActive)
        {
            _parked.Clear();

            // 进入手势时把"停住"的那几笔账清掉：它们本来是为"2→1"准备的，
            // 现在两指都在，接下来的位移全归手势。
        }

        IsActive = true;
        _lastMid = mid;
        _lastDistance = distance;
    }

    private (Point Mid, double Distance) MidpointAndDistance()
    {
        // 只取<b>头两根</b>：第三根落下时（三指）画面不该突然按三指的质心走 ——
        // 白板的手势定义就是"两指"，多出来的手指不参与。
        Point a = default;
        Point b = default;
        var seen = 0;
        foreach (var contact in _contacts.Values)
        {
            if (seen == 0) a = contact;
            else if (seen == 1) b = contact;
            else break;
            seen++;
        }

        return (new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), System.Math.Sqrt(
            (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));
    }
}
