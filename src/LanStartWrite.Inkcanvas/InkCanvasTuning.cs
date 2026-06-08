using System.Diagnostics;
using System.Reflection;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 通过反射调整 <see cref="InkCanvas"/> 内部采样阈值（文档中为 <c>MinPointDistance</c> 字段），
/// 避免默认值过大导致快速书写丢点。若字段不存在或为 const，则静默跳过。
/// </summary>
/// <remarks>
/// 默认不接管 <c>PointerMove</c> 去手动注入 <c>GetIntermediatePoints</c> 的子样本；
/// 先依赖 InkCanvas 内部管线与上述阈值。若在 DEBUG 下观察到 <c>intermediate</c> 计数常大于 1
/// 而笔迹仍明显丢段，再评估 <see cref="InkCanvas.StartDrawing"/> 等手工喂点方案。
/// </remarks>
internal static class InkCanvasTuning
{
    /// <summary>目标最小点距（像素），与计划中的 0.5–1.0 区间一致。</summary>
    internal const double TargetMinPointDistance = 0.75;

    private static readonly FieldInfo? MinPointDistanceField =
        typeof(InkCanvas).GetField(
            "MinPointDistance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

    /// <summary>
    /// 在应用启动时尝试写入静态字段；若为实例字段则在 <paramref name="canvas"/> 上再试一次。
    /// </summary>
    internal static void ApplyStartupDefaults(InkCanvas? canvas = null)
    {
        var f = MinPointDistanceField;
        if (f is null || f.IsInitOnly)
            return;

        try
        {
            if (f.IsStatic)
                f.SetValue(null, TargetMinPointDistance);
            else if (canvas is not null)
                f.SetValue(canvas, TargetMinPointDistance);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InkCanvasTuning] MinPointDistance not set: {ex.Message}");
        }
    }

    internal static void ApplyRuntimeMinPointDistance(double minPointDistance, InkCanvas? canvas = null)
    {
        var f = MinPointDistanceField;
        if (f is null || f.IsInitOnly)
            return;

        try
        {
            if (f.IsStatic)
                f.SetValue(null, minPointDistance);
            else if (canvas is not null)
                f.SetValue(canvas, minPointDistance);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InkCanvasTuning] runtime MinPointDistance not set: {ex.Message}");
        }
    }
}
