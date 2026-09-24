using System.Collections.Generic;
using Jalium.UI;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 白板上的<b>选择层</b>：选框、八向手柄、旋转柄、正在拖的框选矩形 / 套索轨迹。
/// <para>
/// 为什么在应用侧画：闭源的 <c>Dusk.dll</c> 里有选择的数据半边（<c>SelectAt</c> / <c>SelectRect</c> /
/// <c>InkSelection.Translate</c> 那一套），<b>没有选择视觉</b> —— 元素级的那套在
/// <c>Dusk.Ink.Elements</c>，而它有意不并进入包产物。所以"看得见的部分"是宿主的活。
/// </para>
/// <para>
/// <b>所有坐标都是屏幕（本元素本地）坐标</b>，由 <c>WhiteboardWindow</c> 用
/// <c>InkCanvasView.WorldToScreen</c> 换算之后交进来。理由有两条：
/// 一是手柄必须<b>屏幕恒定</b>（放大两倍之后手柄跟着变大的话，就点不中了）；
/// 二是这块面身上不许加变换 —— 视口矩阵是引擎自己写在它内部层载体上的，宿主再包一层
/// 就把墨迹变换两遍、落点也错位了。
/// </para>
/// <para>
/// <c>IsHitTestVisible = false</c>：命中判断是白板窗口按坐标自己算的（<see cref="HandleAt"/> 那一类），
/// 让这层参与命中只会把笔菜单与双指输入的落点搅乱。
/// </para>
/// </summary>
internal sealed class SelectionAdorner : FrameworkElement
{
    /// <summary>手柄与选框那根线的颜色。三档浅底上都读得出来，且与色板里的"蓝色"同值。</summary>
    private static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD4);

    private static readonly Pen FramePen = CreatePen(Accent, 1.5);
    private static readonly Pen HandlePen = CreatePen(Accent, 1.5);
    private static readonly Brush HandleBrush = new SolidColorBrush(Colors.White);
    private static readonly Pen MarqueePen = CreatePen(Accent, 1.0);
    private static readonly Brush MarqueeFill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD4));

    private static Pen CreatePen(Color color, double thickness) =>
        new(new SolidColorBrush(color), thickness);

    internal SelectionAdorner()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>
    /// 选框的四个角（屏幕坐标，<b>顺时针从左上</b>）。为空就不画选框。
    /// 存四角而不是存 <c>Rect</c>：旋转之后它是个平行四边形，用 Rect 表达会"越转越大"。
    /// </summary>
    internal IReadOnlyList<Point>? FrameCorners
    {
        get => _frameCorners;
        set
        {
            if (ReferenceEquals(_frameCorners, value)) return;
            _frameCorners = value;
            InvalidateVisual();
        }
    }

    private IReadOnlyList<Point>? _frameCorners;

    /// <summary>手柄中心（屏幕坐标），顺序即 <see cref="WhiteboardWindow.HandleOrder"/>。为空就不画手柄。</summary>
    internal IReadOnlyList<Point>? Handles
    {
        get => _handles;
        set
        {
            if (ReferenceEquals(_handles, value)) return;
            _handles = value;
            InvalidateVisual();
        }
    }

    private IReadOnlyList<Point>? _handles;

    /// <summary>正在拖的框选矩形（屏幕坐标）。为 null 就不画。</summary>
    internal Rect? Marquee
    {
        get => _marquee;
        set
        {
            if (_marquee == value) return;
            _marquee = value;
            InvalidateVisual();
        }
    }

    private Rect? _marquee;

    /// <summary>正在拖的套索轨迹（屏幕坐标）。少于两点就不画。</summary>
    internal IReadOnlyList<Point>? LassoPoints
    {
        get => _lassoPoints;
        set
        {
            if (ReferenceEquals(_lassoPoints, value)) return;
            _lassoPoints = value;
            InvalidateVisual();
        }
    }

    private IReadOnlyList<Point>? _lassoPoints;

    /// <summary>手柄在界面上的半径（DIP）。<b>不随缩放变</b> —— 这是整层放在屏幕空间画的全部理由。</summary>
    internal const double HandleRadius = 5;

    /// <summary>旋转柄离顶边的那一段（DIP），同样屏幕恒定。</summary>
    internal const double RotateHandleOffset = 18;

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext context)
    {
        var marquee = Marquee;
        if (marquee is { } box && box.Width > 0.5 && box.Height > 0.5)
        {
            context.DrawRectangle(MarqueeFill, MarqueePen, box);
        }

        if (_lassoPoints is { Count: >= 2 } lasso)
        {
            for (var i = 1; i < lasso.Count; i++)
            {
                context.DrawLine(MarqueePen, lasso[i - 1], lasso[i]);
            }

            if (lasso.Count > 2)
            {
                context.DrawLine(MarqueePen, lasso[^1], lasso[0]);
            }
        }

        if (_frameCorners is { Count: 4 } corners)
        {
            for (var i = 0; i < 4; i++)
            {
                context.DrawLine(FramePen, corners[i], corners[(i + 1) % 4]);
            }
        }

        if (_handles is { Count: > 0 } handles)
        {
            var size = HandleRadius * 2;
            foreach (var point in handles)
            {
                context.DrawRectangle(
                    HandleBrush,
                    HandlePen,
                    new Rect(point.X - HandleRadius, point.Y - HandleRadius, size, size));
            }
        }
    }
}
