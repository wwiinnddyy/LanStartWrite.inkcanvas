namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 画布设置：<b>当前场景的那一套开关</b>。
/// <para>
/// 与 <see cref="InkRuntimeOptions"/> / <see cref="InkTipOptions"/> 同一个形状 ——
/// 应用侧的状态所有者（唯一一份），配一个 <see cref="Changed"/> 给宿主。
/// 数据本身按场景存在 <see cref="PreferenceSnapshot.CanvasScenes"/> 里，本类只是"当前这一个场景"的视图。
/// </para>
/// <para>
/// <b>为什么不做成两个静态布尔</b>：开关是<b>按场景</b>的。屏幕批注开了冻结，不代表以后的白板也该冻结 ——
/// 而"每个场景各有各的画布"正是用户提的这件事。视图这一层因此必须知道自己在看哪个场景。
/// </para>
/// </summary>
internal static class CanvasOptions
{
    private static readonly Dictionary<CanvasScene, CanvasSceneSettings> Settings = [];

    private static CanvasScene _scene = CanvasScene.ScreenAnnotation;
    private static bool _loading;

    /// <summary>任一开关变了。宿主据此重新决定"现在该不该显示画布、怎么显示"。</summary>
    internal static event Action? Changed;

    /// <summary>当前场景。现在只有屏幕批注，将来切换场景就是改它。</summary>
    internal static CanvasScene Scene => _scene;

    /// <summary>当前场景的穿透模式。见 <see cref="CanvasSceneSettings.PassThrough"/> 的说明。</summary>
    internal static bool PassThrough => Current().PassThrough;

    /// <summary>当前场景的冻结模式。见 <see cref="CanvasSceneSettings.Freeze"/> 的说明。</summary>
    internal static bool Freeze => Current().Freeze;

    internal static void SetPassThrough(bool value) => Update(_scene, settings => settings with { PassThrough = value });

    internal static void SetFreeze(bool value) => Update(_scene, settings => settings with { Freeze = value });

    /// <summary>从存档装回。缺的场景按默认值补一个，"没配过"与"全关"不是一回事。</summary>
    internal static void Load(CanvasSceneCollection collection)
    {
        _loading = true;
        try
        {
            Settings.Clear();
            foreach (var settings in collection.Items ?? [])
                Settings[settings.Scene] = settings;
        }
        finally { _loading = false; }
    }

    /// <summary>当前全部场景的设置，交给存档。</summary>
    internal static CanvasSceneCollection Snapshot()
    {
        var items = new List<CanvasSceneSettings>();
        foreach (var scene in Enum.GetValues<CanvasScene>())
        {
            var settings = Settings.TryGetValue(scene, out var stored)
                ? stored
                : new CanvasSceneSettings { Scene = scene };
            items.Add(settings);
        }

        return new CanvasSceneCollection { Items = items };
    }

    /// <summary>某一场景的设置（查不到就按默认值）。给设置页逐场景画开关用。</summary>
    internal static CanvasSceneSettings For(CanvasScene scene) =>
        Settings.TryGetValue(scene, out var settings) ? settings : new CanvasSceneSettings { Scene = scene };

    private static CanvasSceneSettings Current() => For(_scene);

    private static void Update(CanvasScene scene, Func<CanvasSceneSettings, CanvasSceneSettings> edit)
    {
        var current = For(scene);
        var updated = edit(current);
        if (updated == current) return;

        Settings[scene] = updated with { Scene = scene };
        if (_loading) return;

        // 只有"当前场景"变了才需要宿主反应：改别的场景的开关不该动眼前这块画布。
        if (scene == _scene) Changed?.Invoke();
    }
}
