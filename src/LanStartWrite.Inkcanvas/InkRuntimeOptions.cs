namespace LanStartWrite.Inkcanvas;

internal enum InkSmoothingLevel
{
    Low = 0,
    Balanced = 1,
    High = 2,
}

internal sealed record InkRuntimeSnapshot(
    bool EnableRealtimeSampling,
    bool EnableLegacyPostProcessFallback,
    bool EnablePressure,
    bool EnableTilt,
    InkSmoothingLevel SmoothingLevel,
    double MinPointDistance);

internal static class InkRuntimeOptions
{
    private static readonly object Gate = new();

    private static bool _enableRealtimeSampling = true;
    private static bool _enableLegacyPostProcessFallback = true;
    private static bool _enablePressure;
    private static bool _enableTilt;
    private static InkSmoothingLevel _smoothingLevel = InkSmoothingLevel.Balanced;
    private static double _minPointDistance = InkCanvasTuning.TargetMinPointDistance;

    internal static event Action<InkRuntimeSnapshot>? Changed;

    internal static InkRuntimeSnapshot Current
    {
        get
        {
            lock (Gate)
            {
                return BuildSnapshot();
            }
        }
    }

    internal static void SetRealtimeSampling(bool enabled) =>
        SetValue(ref _enableRealtimeSampling, enabled);

    internal static void SetLegacyPostProcessFallback(bool enabled) =>
        SetValue(ref _enableLegacyPostProcessFallback, enabled);

    internal static void SetEnablePressure(bool enabled) =>
        SetValue(ref _enablePressure, enabled);

    internal static void SetEnableTilt(bool enabled) =>
        SetValue(ref _enableTilt, enabled);

    internal static void SetSmoothingLevel(InkSmoothingLevel level) =>
        SetValue(ref _smoothingLevel, level);

    internal static void SetMinPointDistance(double value)
    {
        var clamped = Math.Clamp(value, 0.4, 2.5);
        SetValue(ref _minPointDistance, clamped);
    }

    private static void SetValue<T>(ref T field, T value)
    {
        InkRuntimeSnapshot snapshot;
        lock (Gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            snapshot = BuildSnapshot();
        }

        Changed?.Invoke(snapshot);
    }

    private static InkRuntimeSnapshot BuildSnapshot()
    {
        return new InkRuntimeSnapshot(
            _enableRealtimeSampling,
            _enableLegacyPostProcessFallback,
            _enablePressure,
            _enableTilt,
            _smoothingLevel,
            _minPointDistance);
    }
}
