namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 画布设置：<b>按场景存的那几套开关</b>，数据落在 <see cref="PreferenceSnapshot.CanvasScenes"/>。
/// <para>
/// 与 <see cref="InkRuntimeOptions"/> / <see cref="InkTipOptions"/> 一样是应用侧的状态所有者（唯一一份），
/// 配一个 <see cref="Changed"/> 给宿主。但它<b>没有"当前场景"这一层</b>：
/// 存取一律点名场景，"此刻哪块在眼前"在 <see cref="CanvasSceneState"/>。
/// </para>
/// <para>
/// <b>为什么把游标拿掉</b>（原来这里有 <c>Scene</c> / <c>PassThrough</c> / <c>Freeze</c> 三个"当前场景"视图）：
/// 两块画布同时在内存里之后，"当前"这个词就有两个意思了 —— 设置页要一次看两个场景的行，
/// 而画布只该按自己那一套表现。留一个游标就得让设置页绕过它去读 <c>For(scene)</c>，
/// 于是同一份数据出现两条读法，而第二条才是全的。现在只有一条。
/// </para>
/// <para>
/// 连带后果：<c>Changed</c> 现在<b>如实报是哪个场景变了</b>，不在这里替消费方决定"要不要理"。
/// 画布那侧自己用 <see cref="CanvasSceneState.IsActive"/> 挡（这条闸原来藏在本类的 <c>Update</c> 里）。
/// </para>
/// </summary>
internal static class CanvasOptions
{
    private static readonly Dictionary<CanvasScene, CanvasSceneSettings> Settings = [];
    private static bool _loading;

    /// <summary>某个场景的设置变了。宿主据此决定"这一块画布现在该怎么表现"。</summary>
    internal static event Action<CanvasScene>? Changed;

    /// <summary>某个场景的穿透模式。见 <see cref="CanvasSceneSettings.PassThrough"/> 的说明。</summary>
    internal static void SetPassThrough(CanvasScene scene, bool value) =>
        Update(scene, settings => settings with { PassThrough = value });

    /// <summary>某个场景的冻结模式。见 <see cref="CanvasSceneSettings.Freeze"/> 的说明。</summary>
    internal static void SetFreeze(CanvasScene scene, bool value) =>
        Update(scene, settings => settings with { Freeze = value });

    /// <summary>某个场景的底色。见 <see cref="CanvasSceneSettings.BackgroundArgb"/> 的说明（当场生效）。</summary>
    internal static void SetBackground(CanvasScene scene, uint argb) =>
        Update(scene, settings => settings with { BackgroundArgb = CanvasBackgroundPalette.Normalize(argb) });

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

    /// <summary>当前全部场景的设置，交给存档。<b>整份写</b>：只写改过的那一项会把别的场景抹掉。</summary>
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

    /// <summary>某一场景的设置（查不到就按默认值）。给设置页逐场景画开关、给画布读自己那一套。</summary>
    internal static CanvasSceneSettings For(CanvasScene scene) =>
        Settings.TryGetValue(scene, out var settings) ? settings : new CanvasSceneSettings { Scene = scene };

    private static void Update(CanvasScene scene, Func<CanvasSceneSettings, CanvasSceneSettings> edit)
    {
        var current = For(scene);
        var updated = edit(current);
        if (updated == current) return;

        Settings[scene] = updated with { Scene = scene };
        if (_loading) return;

        Changed?.Invoke(scene);
    }
}
