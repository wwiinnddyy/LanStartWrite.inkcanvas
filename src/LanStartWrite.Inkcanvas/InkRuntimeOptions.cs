namespace LanStartWrite.Inkcanvas;

internal sealed record InkRuntimeSnapshot(bool EnablePressure);

internal static class InkRuntimeOptions
{
    private static readonly object Gate = new();

    private static bool _enablePressure;

    internal static event Action<InkRuntimeSnapshot>? Changed;

    internal static InkRuntimeSnapshot Current
    {
        get
        {
            lock (Gate)
            {
                return new InkRuntimeSnapshot(_enablePressure);
            }
        }
    }

    internal static void SetEnablePressure(bool enabled)
    {
        InkRuntimeSnapshot snapshot;
        lock (Gate)
        {
            if (_enablePressure == enabled)
                return;
            _enablePressure = enabled;
            snapshot = new InkRuntimeSnapshot(enabled);
        }

        Changed?.Invoke(snapshot);
    }
}
