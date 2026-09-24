using System;
using Dusk.Ink.Primitives;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 选框：一块<b>会转、会缩</b>的矩形，存的是"中心 + 两半轴 + 转角"（世界系）。
/// <para>
/// <b>为什么不能每帧从 <c>InkSelection.Bounds</c> 反推</b>：那个是"选中墨迹的正立（axis-aligned）
/// 外包盒"，把选中的东西转过 45° 之后再读它，得到的是包住旋转形状的那块更大的正方形 ——
/// 手柄会随转角越变越远，第二次拖同一个角就已经不是它了。所以选框是一份<b>独立状态</b>：
/// 只在"选择集变了"的那一刻从 Bounds 取一次，之后跟着用户的拖动走。
/// </para>
/// <para>
/// 单位一律是<b>世界</b>：这样缩放画面时选框跟着内容走（这是对的），
/// 而手柄的<b>尺寸</b>不进这里 —— 它由 <see cref="SelectionAdorner"/> 按屏幕恒定画。
/// </para>
/// </summary>
internal sealed class SelectionFrame
{
    /// <summary>最小半轴（世界单位）。拖到 0 会让因子变成除不尽的怪数，宁可留一条缝。</summary>
    private const double MinHalf = 0.001;

    internal SelectionFrame(Point2D center, double halfWidth, double halfHeight, double angleRadians)
    {
        Center = center;
        HalfWidth = halfWidth;
        HalfHeight = halfHeight;
        Angle = angleRadians;
    }

    internal Point2D Center;
    internal double HalfWidth;
    internal double HalfHeight;

    /// <summary>累计转角（弧度）。每次拖旋转柄只加一个增量，不从"绝对角度"反算 —— 绝对角要拿文档里的点去猜。</summary>
    internal double Angle;

    internal bool IsEmpty => HalfWidth <= 0 || HalfHeight <= 0;

    /// <summary>世界系的正立包围盒（未旋转时就是选框本身）。</summary>
    internal static SelectionFrame FromWorldBounds(Rect2D bounds)
    {
        if (bounds == Rect2D.Empty) return new SelectionFrame(new Point2D(0, 0), 0, 0, 0);

        return new SelectionFrame(
            new Point2D(bounds.CenterX, bounds.CenterY),
            Math.Max(MinHalf, bounds.Width * 0.5),
            Math.Max(MinHalf, bounds.Height * 0.5),
            0);
    }

    internal void Translate(double dx, double dy) => Center = new Point2D(Center.X + dx, Center.Y + dy);

    /// <summary>局部坐标（相对中心，沿选框自己的两根轴）→ 世界。</summary>
    internal Point2D LocalToWorld(Point2D local)
    {
        var cos = Math.Cos(Angle);
        var sin = Math.Sin(Angle);
        return new Point2D(
            Center.X + local.X * cos - local.Y * sin,
            Center.Y + local.X * sin + local.Y * cos);
    }

    /// <summary>
    /// 世界系的<b>向量</b>折到选框自己的两根轴上。<b>只转不移</b> ——
    /// 拖动的位移是一个向量，不能用 <see cref="WorldToLocal"/>（那是给点用的，会扣掉中心），
    /// 否则同一帧的位移会被算成"从中心出发的位置"，缩放因子立刻飞掉。
    /// </summary>
    internal Point2D WorldVectorToLocal(double vectorX, double vectorY)
    {
        var cos = Math.Cos(-Angle);
        var sin = Math.Sin(-Angle);
        return new Point2D(vectorX * cos - vectorY * sin, vectorX * sin + vectorY * cos);
    }

    /// <summary>世界 → 局部（沿选框自己的两根轴）。<b>拖动的位移要用这个折算</b>：转过 90° 之后"往右拖"是选框的"往下"。</summary>
    internal Point2D WorldToLocal(Point2D world)
    {
        var cos = Math.Cos(-Angle);
        var sin = Math.Sin(-Angle);
        var dx = world.X - Center.X;
        var dy = world.Y - Center.Y;
        return new Point2D(dx * cos - dy * sin, dx * sin + dy * cos);
    }

    /// <summary>
    /// 手柄编号：<b>0 左上、1 上、2 右上、3 右、4 右下、5 下、6 左下、7 左</b>（顺时针），
    /// <b>8 是旋转柄</b>（顶边中点再往上那一截）。顺序即 <see cref="SelectionAdorner.Handles"/> 的顺序 ——
    /// 画出来的那颗与点得中的那颗必须同一个下标，所以这张表只有这一份。
    /// </summary>
    internal const int HandleCount = 8;

    internal const int RotateHandle = 8;

    /// <summary>这个手柄往<b>哪个方向拖是变大</b>：±1 表示沿该轴的外侧，0 表示这一轴不动。</summary>
    private static (int X, int Y) AxisSign(int handle) => handle switch
    {
        0 => (-1, -1),
        1 => (0, -1),
        2 => (1, -1),
        3 => (1, 0),
        4 => (1, 1),
        5 => (0, 1),
        6 => (-1, 1),
        7 => (-1, 0),
        _ => (0, 0),
    };

    /// <summary>手柄（含旋转柄）的局部坐标。<paramref name="rotateOffsetWorld"/> 是旋转柄超出顶边的那一段。</summary>
    internal Point2D HandleLocal(int handle, double rotateOffsetWorld = 0)
    {
        if (handle == RotateHandle) return new Point2D(0, -HalfHeight - rotateOffsetWorld);

        var (ax, ay) = AxisSign(handle);
        return new Point2D(ax * HalfWidth, ay * HalfHeight);
    }

    internal Point2D HandleWorld(int handle, double rotateOffsetWorld = 0) =>
        LocalToWorld(HandleLocal(handle, rotateOffsetWorld));

    /// <summary>对角那一头（缩放的不动点）：顶角 → 另一个顶角，边 → 对边中点。</summary>
    private Point2D OppositeLocal(int handle)
    {
        var (ax, ay) = AxisSign(handle);
        return new Point2D(-ax * HalfWidth, -ay * HalfHeight);
    }

    /// <summary>
    /// 一次缩放意图：<b>沿选框自己的两根轴</b>各乘一个因子，且对角那一头的<b>世界点不动</b>。
    /// <para>
    /// 返回的三件套是"该叫引擎做什么"：<see cref="AnchorWorld"/> 是那个要钉住的点，
    /// 而 <see cref="PreRotationRadians"/> / <see cref="PostRotationRadians"/> 是绕中心回转与回正 ——
    /// 因为引擎的 <c>Scale</c> 只认世界轴，非零转角下必须"先转平、再缩放、再转回去"三步，
    /// 一步做完会把长宽按错轴乘（这条推起来不显然，验收入口钉的就是"对角那一角的世界坐标不动"）。
    /// </para>
    /// </summary>
    internal bool TryScale(int handle, Point2D localDelta, double rotateOffsetWorld, out ScaleRequest request)
    {
        request = default;
        var (ax, ay) = AxisSign(handle);
        if (ax == 0 && ay == 0) return false;

        // 手指走的那一段是<b>整宽</b>的变化（对角钉住，另一头跟着走一半、中心走一半），
        // 而这里存的是<b>半轴</b> —— 不折半的话，拖 60 会真的长出 120（右下角跑到手指前面 60 处）。
        var nextWidth = ax == 0 ? HalfWidth : Math.Max(MinHalf, HalfWidth + localDelta.X * ax * 0.5);
        var nextHeight = ay == 0 ? HalfHeight : Math.Max(MinHalf, HalfHeight + (localDelta.Y * ay * 0.5));
        var sx = nextWidth / HalfWidth;
        var sy = nextHeight / HalfHeight;
        if (Math.Abs(sx - 1) < 1e-9 && Math.Abs(sy - 1) < 1e-9) return false;

        var opposite = OppositeLocal(handle);
        request = new ScaleRequest(
            sx, sy,
            LocalToWorld(opposite),
            opposite,
            new Point2D(opposite.X * sx, opposite.Y * sy));
        return true;
    }

    /// <summary>缩放这一步的参数。字段含义见 <see cref="TryScale"/>。</summary>
    internal readonly struct ScaleRequest
    {
        internal ScaleRequest(double scaleX, double scaleY, Point2D anchorWorld, Point2D oppositeLocal, Point2D scaledOppositeLocal)
        {
            ScaleX = scaleX;
            ScaleY = scaleY;
            AnchorWorld = anchorWorld;
            OppositeLocal = oppositeLocal;
            ScaledOppositeLocal = scaledOppositeLocal;
        }

        internal double ScaleX { get; }
        internal double ScaleY { get; }

        /// <summary>要钉住的那个世界点（对角那一头）。</summary>
        internal Point2D AnchorWorld { get; }

        /// <summary>对角的局部坐标 —— 转平之后它就是那个世界点相对中心的偏移。</summary>
        internal Point2D OppositeLocal { get; }

        /// <summary>缩放之后对角落在哪（局部）。</summary>
        internal Point2D ScaledOppositeLocal { get; }
    }

    /// <summary>把选框自身推到缩放之后的样子（中心按"对角钉住"反算，两半轴各乘因子）。</summary>
    internal void ApplyScale(ScaleRequest request)
    {
        // C' = C + A − R·(la ⊙ s)：对角那个世界点 A 不动，中心按"缩放之后的对角位置"反推。
        // 依据只有这一条：LocalToWorld(v) = C + R·v，所以 R·(la⊙s) = LocalToWorld(la⊙s) − C。
        var anchor = LocalToWorld(request.OppositeLocal);
        var scaledOpposite = LocalToWorld(request.ScaledOppositeLocal);
        Center = new Point2D(
            Center.X + anchor.X - scaledOpposite.X,
            Center.Y + anchor.Y - scaledOpposite.Y);
        HalfWidth = Math.Max(MinHalf, HalfWidth * request.ScaleX);
        HalfHeight = Math.Max(MinHalf, HalfHeight * request.ScaleY);
    }

    /// <summary>转角加一个增量（中心不动）。</summary>
    internal void ApplyRotation(double deltaRadians) => Angle += deltaRadians;
}
