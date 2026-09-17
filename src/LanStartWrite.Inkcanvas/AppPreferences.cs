using System.Diagnostics;
using System.Text.Json;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

internal enum AppTheme { Light, Dark, System }

internal sealed record PreferenceSnapshot
{
    public AppTheme Theme { get; init; } = AppTheme.Light;
    public bool ReduceMotion { get; init; }
    public bool KeepToolbarOnTop { get; init; } = true;
    public double PenWidth { get; init; } = 4;
    public bool RealtimeSampling { get; init; } = true;
    public bool Pressure { get; init; }
    public bool Tilt { get; init; }
    public InkSmoothingLevel Smoothing { get; init; } = InkSmoothingLevel.Balanced;
    public double MinPointDistance { get; init; } = 0.75;
}

/// <summary>UI-thread-owned preferences. Slider changes are debounced; writes replace atomically.</summary>
internal static class AppPreferences
{
    private static string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanStartWrite", "preferences.json");
    private static DispatcherTimer? _saveTimer;
    private static bool _initialized;
    private static bool _pending;
    private static bool _applyingInkOptions;
    internal static bool IsSavePending => _pending;
    internal static PreferenceSnapshot Current { get; private set; } = new();
    internal static string? SaveError { get; private set; }
    internal static event Action<PreferenceSnapshot>? Changed;
    internal static event Action? SaveStatusChanged;

    internal static void Initialize(string? storagePath = null)
    {
        if (_initialized) return;
        _initialized = true;
        if (storagePath is not null) FilePath = Path.GetFullPath(storagePath);
        try
        {
            if (File.Exists(FilePath))
                Current = Validate(JsonSerializer.Deserialize<PreferenceSnapshot>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SaveError = "无法读取偏好设置，本次使用默认值。";
            Trace.WriteLine(ex);
        }
        ApplyInkOptions(Current);
        InkRuntimeOptions.Changed += options =>
        {
            if (_applyingInkOptions) return;
            Update(Current with
            {
                RealtimeSampling = options.EnableRealtimeSampling,
                Pressure = options.EnablePressure,
                Tilt = options.EnableTilt,
                Smoothing = options.SmoothingLevel,
                MinPointDistance = options.MinPointDistance,
            });
        };
    }

    internal static void Update(PreferenceSnapshot value)
    {
        value = Validate(value);
        if (value == Current) return;
        Current = value;
        _applyingInkOptions = true;
        try { ApplyInkOptions(value); }
        finally { _applyingInkOptions = false; }
        _pending = true;
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(value);
        SaveStatusChanged?.Invoke();
    }

    private static DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => Flush();
        return timer;
    }

    internal static void Flush()
    {
        _saveTimer?.Stop();
        if (!_pending) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
            SaveError = null;
            _pending = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = "偏好设置未能保存；本次会话内仍然生效。";
            Trace.WriteLine(ex);
        }
        SaveStatusChanged?.Invoke();
    }

    private static PreferenceSnapshot Validate(PreferenceSnapshot value) => value with
    {
        Theme = Enum.IsDefined(value.Theme) ? value.Theme : AppTheme.Light,
        Smoothing = Enum.IsDefined(value.Smoothing) ? value.Smoothing : InkSmoothingLevel.Balanced,
        PenWidth = double.IsFinite(value.PenWidth) ? Math.Clamp(Math.Round(value.PenWidth), 1, 24) : 4,
        MinPointDistance = double.IsFinite(value.MinPointDistance) ? Math.Clamp(value.MinPointDistance, 0.4, 2.5) : 0.75,
    };

    private static void ApplyInkOptions(PreferenceSnapshot value)
    {
        InkRuntimeOptions.SetRealtimeSampling(value.RealtimeSampling);
        InkRuntimeOptions.SetEnablePressure(value.Pressure);
        InkRuntimeOptions.SetEnableTilt(value.Tilt);
        InkRuntimeOptions.SetSmoothingLevel(value.Smoothing);
        InkRuntimeOptions.SetMinPointDistance(value.MinPointDistance);
    }
}
