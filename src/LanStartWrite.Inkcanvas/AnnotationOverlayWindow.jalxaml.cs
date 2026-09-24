using System.Diagnostics;
using Dusk.Adapter.Jalium;
using Dusk.Ink.Controls;
using Dusk.Ink.Document;
using Dusk.Ink.Model;
using Dusk.Ink.Primitives;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationOverlayWindow : Window
{
    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private readonly JaliumInkCanvas _surface = new();
    private readonly InkHistory _history;
    private PenKind _currentKind = PenKind.Pen;
    private Color _currentColor = Colors.Black;
    private double _currentThickness = 3;
    private EraserMode _eraserMode = EraserMode.Area;

    public AnnotationOverlayWindow()
    {
        AllowsTransparency = true;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        Opacity = 1;
        Background = TransparentBrush;
        InitializeComponent();

        InkHost.Children.Add(_surface);

        // 撤销/重做挂在文档上：书写、擦除、清空、整笔擦都进同一条历史。
        _history = new InkHistory(_surface.Document);
        _surface.Document.AttachHistory(_history);
        _surface.Document.Changed += (_, _) => HistoryStateChanged?.Invoke();

        ApplyAttributes();
        ApplyTipOptions();
        InkRuntimeOptions.Changed += OnInkRuntimeOptionsChanged;
        InkTipOptions.Changed += OnInkTipOptionsChanged;
        ApplyRuntimeOptions(InkRuntimeOptions.Current);
        Closed += OnClosed;

#if DEBUG
        // 可见区由 ArrangeOverride 报进来的尺寸算出；格子塌成零尺寸时画面全空且不报错。
        _surface.Loaded += (_, _) => Debug.Assert(
            _surface.ActualWidth > 0, "ink host arranged to zero size: nothing will render");
        _surface.StrokeCommitted += (_, _) => Debug.WriteLine(
            $"[ink-metrics] doc={_surface.Document.Count} passes={_surface.RenderPassCount} "
            + $"last={_surface.LastInputToRenderMs:F1}ms peak={_surface.PeakInputToRenderMs:F1}ms");
#endif
    }

    public void SetInkMode()
    {
        _surface.IsEraserMode = false;
    }

    public void SetEraseMode()
    {
        SetEraserMode(_eraserMode);
    }

    /// <summary>换橡皮的擦法：面积擦＝引擎点擦，笔迹擦＝整笔摘除。</summary>
    public void SetEraserMode(EraserMode mode)
    {
        _eraserMode = mode;
        _surface.EditingMode = mode == EraserMode.Stroke
            ? InkEditingMode.EraseByStroke
            : InkEditingMode.EraseByPoint;
    }

    public void SetEraserRadius(double radius)
    {
        _surface.EraserRadius = double.IsFinite(radius) ? Math.Clamp(radius, 4, 48) : 14;
    }

    public void ClearCanvas() => _surface.Clear();

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public void Undo() => _history.Undo();

    public void Redo() => _history.Redo();

    /// <summary>文档变了（因而可撤销/可重做的东西也变了）。宿主工具栏据此刷按钮状态。</summary>
    public event Action? HistoryStateChanged;

    public void SetPenKind(PenKind kind)
    {
        _currentKind = kind;
        ApplyAttributes();
    }

    public void SetPenColor(Color color)
    {
        _currentColor = color;
        ApplyAttributes();
    }

    public void SetPenThickness(double thickness)
    {
        _currentThickness = Math.Max(1, thickness);
        ApplyAttributes();
    }

    private void ApplyAttributes()
    {
        var da = _surface.InkAttributes;
        da.Kind = KindFor(_currentKind);
        da.Color = new InkColor(
            _currentColor.R,
            _currentColor.G,
            _currentColor.B,
            AlphaFor(_currentKind));
        da.Width = _currentThickness;
        da.Height = _currentThickness;
    }

    private static StrokeKind KindFor(PenKind kind) => kind switch
    {
        PenKind.Highlighter => StrokeKind.Uniform,
        PenKind.Laser => StrokeKind.Laser,
        _ => StrokeKind.VariableWidth,
    };

    private static byte AlphaFor(PenKind kind) => kind switch
    {
        PenKind.Highlighter => InkBrushes.HighlighterAlpha,
        PenKind.Laser => InkBrushes.LaserAlpha,
        _ => byte.MaxValue,
    };

    private void OnInkRuntimeOptionsChanged(InkRuntimeSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => ApplyRuntimeOptions(snapshot));
    }

    private void ApplyRuntimeOptions(InkRuntimeSnapshot snapshot)
    {
        _surface.InkAttributes.IgnorePressure = !snapshot.EnablePressure;
    }

    /// <summary>
    /// 笔锋的注入点只有这一处：把应用侧的笔锋状态写进墨迹控件的 <c>TipSettings</c>。
    /// <para>
    /// 走的是引擎的快照往返（按参数名对齐、批量写），因此参数只改一次就只请求一次重绘。
    /// 排队一拍的理由与墨迹偏好一致 —— 变更可能来自别的窗口的输入事件，
    /// 同一拍里改画布属性会让那一拍的渲染读到半套参数。
    /// </para>
    /// </summary>
    private void OnInkTipOptionsChanged()
    {
        Dispatcher.BeginInvoke(ApplyTipOptions);
    }

    private void ApplyTipOptions()
    {
        InkTipOptions.ApplyTo(_surface.TipSettings);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        InkRuntimeOptions.Changed -= OnInkRuntimeOptionsChanged;
        InkTipOptions.Changed -= OnInkTipOptionsChanged;
        _surface.Dispose();
    }
}
