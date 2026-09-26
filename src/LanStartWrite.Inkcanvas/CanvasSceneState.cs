namespace LanStartWrite.Inkcanvas;

/// <summary>
/// <b>此刻哪块画布在眼前</b>。全应用唯一一份，<b>不落盘</b>。
/// <para>
/// 它与 <see cref="CanvasOptions"/> 是两件事，所以分在两个类里：那边是"每块画布该有怎样的表现"，
/// 是用户配的、要存进存档的；这里只是"当前显示的是哪一块"，是运行时形态。
/// </para>
/// <para>
/// <b>为什么不塞进 <see cref="CanvasOptions"/> 当一个当前场景游标</b>：那个类的
/// <c>Changed</c> 连着存档那条桥（<see cref="AppPreferences"/> 里的
/// <c>CanvasOptions.Changed += CaptureCanvasOptions</c>）。一旦"切到白板"也走它，
/// 一次点按钮就会写一次盘，而切换画布根本不是用户设置；同一个事件还要兼两义
/// （"设置变了"与"眼前换了"），消费方分不清该不该反应。
/// </para>
/// <para>
/// 默认是屏幕批注：这个应用启动后落在批注栏上，白板要用户点一下才进。
/// </para>
/// </summary>
internal static class CanvasSceneState
{
    private static CanvasScene _active = CanvasScene.ScreenAnnotation;

    /// <summary>在眼前的那块画布换了，参数是<b>换过来</b>的那一个。</summary>
    internal static event Action<CanvasScene>? Changed;

    internal static CanvasScene Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Changed?.Invoke(value);
        }
    }

    /// <summary>是不是当前在眼前那块。<b>给消费方过滤用</b>：设置页要听所有场景的变化，
    /// 而画布只该按自己这一块的设置表现。</summary>
    internal static bool IsActive(CanvasScene scene) => _active == scene;

    /// <summary>
    /// 是不是<b>"有页面内容、而且鼠标那一档已经是选择"那种</b>场景。
    /// <para>
    /// 白板与图片批注都是：底下有一页固定尺寸的东西，于是"鼠标"从"把桌面还给别的应用"
    /// 变成"选择这一页上的墨迹"；屏幕批注没有这一层，它盖的是整个桌面，没有"页"。
    /// </para>
    /// <para>
    /// 为什么名单要收在一处而不是到处写 <c>scene != ScreenAnnotation</c>：
    /// 那样每加一个场景都要回头改一串不相干的地方，漏一处就是"那颗钮在这个场景里意思错了"，
    /// 而那句话不报错、只是画出来的图标不对。这里是唯一一份名单。
    /// </para>
    /// </summary>
    internal static bool IsPageScene(CanvasScene scene) =>
        scene is CanvasScene.Whiteboard or CanvasScene.ImageCanvas or CanvasScene.PdfCanvas;
}
