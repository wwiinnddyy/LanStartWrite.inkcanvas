using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Ink;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Threading;

namespace LanStartWrite.Inkcanvas;

public partial class AnnotationOverlayWindow : Window
{
    private bool _isRebuildingStroke;
    private PenKind _currentKind = PenKind.Pen;
    private readonly List<DispatcherTimer> _laserFadeTimers = [];
    private readonly InkInputMetrics _metrics = new();
    private int _lastPointerId = -1;
    private StylusPointCollection? _lastRealtimePoints;
    private PointerDeviceType _lastPointerDeviceType = PointerDeviceType.Mouse;

    private static readonly MethodInfo[] RealtimeFeedMethods =
        typeof(InkCanvas)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly Brush TransparentBrush =
        new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    private InkCanvas Surface => (InkCanvas)OverlayInk!;

    public AnnotationOverlayWindow()
    {
        AllowsTransparency = true;
        ShowActivated = false;
        SystemBackdrop = WindowBackdropType.None;
        Opacity = 1;
        Background = TransparentBrush;
        InitializeComponent();
        Surface.Background = TransparentBrush;

        // MinPointDistance 若为实例字段，在 Surface 构造后再写一次。
        InkCanvasTuning.ApplyStartupDefaults(Surface);

        ApplyDefaultDrawingAttributes();
        Surface.StrokeCollected += Surface_OnStrokeCollected_EnforceSmoothAttributes;
        Surface.PreviewPointerMove += Surface_OnPreviewPointerMove_RealtimeSampling;
        Surface.EditingMode = InkCanvasEditingMode.Ink;

        Closed += (_, _) => CancelLaserFadeTimers();
        InkRuntimeOptions.Changed += OnInkRuntimeOptionsChanged;
        ApplyRuntimeOptions(InkRuntimeOptions.Current);

#if DEBUG
        Surface.PreviewPointerMove += Surface_OnPreviewPointerMove_InkDiag;
#endif
    }

#if DEBUG
    private static void Surface_OnPreviewPointerMove_InkDiag(object? sender, RoutedEventArgs e)
    {
        if (sender is not InkCanvas canvas || e is not PointerMoveEventArgs p)
            return;

        var inter = p.GetIntermediatePoints(canvas);
        Debug.WriteLine(
            $"[ink] device={p.Pointer.PointerDeviceType} intermediate={inter.Count}");
    }
#endif

    private void ApplyDefaultDrawingAttributes()
    {
        var da = Surface.DefaultDrawingAttributes;
        da.Color = Colors.Black;
        da.Width = 3;
        da.Height = 3;
        da.StylusTip = StylusTip.Ellipse;
        da.FitToCurve = true;
        da.BrushType = BrushType.Round;
        da.IgnorePressure = !InkRuntimeOptions.Current.EnablePressure;
        da.IsHighlighter = false;
        SyncDynamicRendererAttributes(da);
    }

    private void SyncDynamicRendererAttributes(DrawingAttributes source)
    {
        var previewDa = Surface.DynamicRenderer.DrawingAttributes;
        previewDa.Color = source.Color;
        previewDa.Width = source.Width;
        previewDa.Height = source.Height;
        previewDa.StylusTip = source.StylusTip;
        previewDa.FitToCurve = source.FitToCurve;
        previewDa.BrushType = source.BrushType;
        previewDa.IgnorePressure = source.IgnorePressure;
        previewDa.IsHighlighter = source.IsHighlighter;
    }

    private void Surface_OnStrokeCollected_EnforceSmoothAttributes(
        object? sender,
        InkCanvasStrokeCollectedEventArgs e)
    {
        var stroke = e.Stroke;
        var da = stroke.DrawingAttributes;
        da.StylusTip = StylusTip.Ellipse;
        da.FitToCurve = true;
        da.IgnorePressure = !InkRuntimeOptions.Current.EnablePressure;
        ApplyBrushTypeAndHighlighterForCurrentKind(da);
        var runtime = InkRuntimeOptions.Current;
        var activeStroke = runtime.EnableLegacyPostProcessFallback
            ? TryRebuildSparseStroke(stroke, runtime, _lastPointerDeviceType) ?? stroke
            : stroke;
        _metrics.OnStrokeCommitted(activeStroke.StylusPoints.Count);
        _metrics.EmitIfNeeded();

        if (_currentKind == PenKind.Laser)
            BeginLaserFade(activeStroke);
    }

    private Stroke? TryRebuildSparseStroke(
        Stroke stroke,
        InkRuntimeSnapshot runtime,
        PointerDeviceType deviceType)
    {
        if (_isRebuildingStroke)
            return null;

        var source = stroke.StylusPoints;
        if (source.Count < 3)
            return null;

        var dense = BuildDensifiedPoints(source, runtime, deviceType);
        if (dense is null || dense.Count <= source.Count)
            return null;

        var replacement = new Stroke(dense, stroke.DrawingAttributes.Clone())
        {
            TaperMode = stroke.TaperMode,
        };

        var strokes = Surface.Strokes;
        var index = strokes.IndexOf(stroke);
        if (index < 0)
            return null;

        try
        {
            _isRebuildingStroke = true;
            strokes[index] = replacement;
            _metrics.RebuiltStrokeCount++;
            return replacement;
        }
        finally
        {
            _isRebuildingStroke = false;
        }
    }

    private void BeginLaserFade(Stroke stroke)
    {
        const int holdMs = 600;
        const int fadeMs = 600;
        const int ticks = 12;

        var startColor = stroke.DrawingAttributes.Color;
        if (startColor.A == 0)
            return;

        var stepAlpha = startColor.A / (double)ticks;

        var hold = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(holdMs),
        };
        _laserFadeTimers.Add(hold);

        hold.Tick += HoldTick;
        hold.Start();

        void HoldTick(object? s, EventArgs e2)
        {
            hold.Tick -= HoldTick;
            hold.Stop();
            _laserFadeTimers.Remove(hold);

            var fade = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds((double)fadeMs / ticks),
            };
            _laserFadeTimers.Add(fade);

            var i = 0;
            fade.Tick += FadeTick;
            fade.Start();

            void FadeTick(object? s2, EventArgs e3)
            {
                i++;
                var a = (byte)Math.Clamp(startColor.A - stepAlpha * i, 0, 255);
                stroke.DrawingAttributes.Color = Color.FromArgb(
                    a,
                    startColor.R,
                    startColor.G,
                    startColor.B);

                if (i < ticks)
                    return;

                fade.Tick -= FadeTick;
                fade.Stop();
                _laserFadeTimers.Remove(fade);

                try
                {
                    Surface.Strokes.Remove(stroke);
                }
                catch
                {
                    // 忽略关闭窗口或集合已释放等情况
                }
            }
        }
    }

    private void CancelLaserFadeTimers()
    {
        foreach (var t in _laserFadeTimers)
            t.Stop();
        _laserFadeTimers.Clear();
        InkRuntimeOptions.Changed -= OnInkRuntimeOptionsChanged;
    }

    public void SetPenKind(PenKind kind)
    {
        _currentKind = kind;
        var da = Surface.DefaultDrawingAttributes;
        ApplyBrushTypeAndHighlighterForCurrentKind(da);
        SyncDynamicRendererAttributes(da);
    }

    /// <summary>
    /// 荧光笔使用 <see cref="BrushType.Marker"/>（宽、半透明笔刷）并打开 <see cref="DrawingAttributes.IsHighlighter"/>；
    /// 书写笔与激光笔使用 <see cref="BrushType.Round"/>。
    /// </summary>
    private void ApplyBrushTypeAndHighlighterForCurrentKind(DrawingAttributes da)
    {
        if (_currentKind == PenKind.Highlighter)
        {
            da.IsHighlighter = true;
            da.BrushType = BrushType.Marker;
        }
        else
        {
            da.IsHighlighter = false;
            da.BrushType = BrushType.Round;
        }
    }

    private static StylusPointCollection? BuildDensifiedPoints(
        StylusPointCollection source,
        InkRuntimeSnapshot runtime,
        PointerDeviceType deviceType)
    {
        if (source.Count < 2)
            return null;

        var minSegmentLength = GetMinSegmentLength(runtime, deviceType);
        var dense = new StylusPointCollection();
        dense.Add(source[0]);

        for (var i = 1; i < source.Count; i++)
        {
            var prev = source[i - 1];
            var current = source[i];
            var dx = current.X - prev.X;
            var dy = current.Y - prev.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (distance > minSegmentLength)
            {
                var insertCount = Math.Clamp((int)(distance / minSegmentLength), 1, 8);
                for (var k = 1; k < insertCount; k++)
                {
                    var t = (double)k / insertCount;
                    var x = prev.X + (dx * t);
                    var y = prev.Y + (dy * t);
                    dense.Add(new StylusPoint(x, y));
                }
            }

            dense.Add(current);
        }

        return dense;
    }

    public void SetInkMode()
    {
        Surface.EditingMode = InkCanvasEditingMode.Ink;
    }

    public void SetEraseMode()
    {
        Surface.EditingMode = InkCanvasEditingMode.EraseByStroke;
    }

    public void SetPenColor(Color color)
    {
        var da = Surface.DefaultDrawingAttributes;
        da.Color = color;
        Surface.DynamicRenderer.DrawingAttributes.Color = color;
    }

    public void SetPenThickness(double thickness)
    {
        var t = Math.Max(1, thickness);
        var da = Surface.DefaultDrawingAttributes;
        da.Width = t;
        da.Height = t;
        var previewDa = Surface.DynamicRenderer.DrawingAttributes;
        previewDa.Width = t;
        previewDa.Height = t;
    }

    private void Surface_OnPreviewPointerMove_RealtimeSampling(object? sender, RoutedEventArgs e)
    {
        if (sender is not InkCanvas canvas || e is not PointerMoveEventArgs p)
            return;

        var runtime = InkRuntimeOptions.Current;
        var inter = p.GetIntermediatePoints(canvas);
        if (inter.Count == 0)
            return;

        _metrics.OnIntermediatePoints(inter.Count);
        if (runtime.EnableTilt)
            _metrics.OnTiltSample(TryReadTiltMagnitude(p, canvas));
        if (!runtime.EnableRealtimeSampling || Surface.EditingMode != InkCanvasEditingMode.Ink)
            return;

        var points = new StylusPointCollection();
        foreach (var item in inter)
            points.Add(new StylusPoint(item.Position.X, item.Position.Y));

        var pointerId = p.Pointer.GetHashCode();
        _lastPointerDeviceType = p.Pointer.PointerDeviceType;
        if (pointerId != _lastPointerId)
        {
            _lastPointerId = pointerId;
            _lastRealtimePoints = null;
        }

        if (_lastRealtimePoints is not null && points.Count > 0)
        {
            var first = points[0];
            var prev = _lastRealtimePoints[^1];
            if (Math.Abs(prev.X - first.X) < 0.001 && Math.Abs(prev.Y - first.Y) < 0.001)
                points.RemoveAt(0);
        }

        if (points.Count == 0)
            return;

        _lastRealtimePoints = points;
        TryFeedRealtimePoints(points);
    }

    private void TryFeedRealtimePoints(StylusPointCollection points)
    {
        // 优先探测 Jalium InkCanvas 可用的实时喂点入口；若当前版本未公开对应 API，保持兼容降级。
        foreach (var m in RealtimeFeedMethods)
        {
            if (m.Name is not ("AddPoints" or "AppendPoints" or "FeedPoints" or "UpdateDrawing"))
                continue;

            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(StylusPointCollection)))
            {
                try
                {
                    m.Invoke(Surface, [points]);
                }
                catch
                {
                    // ignore and continue fallback
                }

                return;
            }
        }
    }

    private static double GetMinSegmentLength(
        InkRuntimeSnapshot runtime,
        PointerDeviceType deviceType)
    {
        var baseLength = runtime.SmoothingLevel switch
        {
            InkSmoothingLevel.Low => Math.Max(0.95, runtime.MinPointDistance * 1.35),
            InkSmoothingLevel.High => Math.Max(0.45, runtime.MinPointDistance * 0.75),
            _ => Math.Max(0.65, runtime.MinPointDistance),
        };

        return deviceType switch
        {
            PointerDeviceType.Pen => baseLength * 0.9,
            PointerDeviceType.Touch => baseLength * 1.1,
            _ => baseLength,
        };
    }

    private void OnInkRuntimeOptionsChanged(InkRuntimeSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => ApplyRuntimeOptions(snapshot));
    }

    private void ApplyRuntimeOptions(InkRuntimeSnapshot snapshot)
    {
        InkCanvasTuning.ApplyRuntimeMinPointDistance(snapshot.MinPointDistance, Surface);
        var da = Surface.DefaultDrawingAttributes;
        da.IgnorePressure = !snapshot.EnablePressure;
        da.FitToCurve = snapshot.SmoothingLevel != InkSmoothingLevel.Low;
        SyncDynamicRendererAttributes(da);
    }

    private static double TryReadTiltMagnitude(PointerMoveEventArgs p, InkCanvas canvas)
    {
        try
        {
            var point = p.GetCurrentPoint(canvas);
            var props = point.Properties;
            var t = props.GetType();
            var x = t.GetProperty("XTilt")?.GetValue(props) as double?;
            var y = t.GetProperty("YTilt")?.GetValue(props) as double?;
            if (x is null || y is null)
                return 0;
            return Math.Sqrt((x.Value * x.Value) + (y.Value * y.Value));
        }
        catch
        {
            return 0;
        }
    }

    private sealed class InkInputMetrics
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private int _strokeCount;
        private int _strokePointCount;
        private int _intermediatePoints;
        private int _tiltSamples;
        private double _tiltSum;

        internal int RebuiltStrokeCount { get; set; }

        internal void OnIntermediatePoints(int count) => _intermediatePoints += count;

        internal void OnStrokeCommitted(int strokePointCount)
        {
            _strokeCount++;
            _strokePointCount += strokePointCount;
        }

        internal void OnTiltSample(double tiltMagnitude)
        {
            if (tiltMagnitude <= 0)
                return;
            _tiltSamples++;
            _tiltSum += tiltMagnitude;
        }

        internal void EmitIfNeeded()
        {
            if (_watch.Elapsed < TimeSpan.FromSeconds(3))
                return;

            var avgStrokePoints = _strokeCount == 0 ? 0 : _strokePointCount / (double)_strokeCount;
            var avgTilt = _tiltSamples == 0 ? 0 : _tiltSum / _tiltSamples;
            Debug.WriteLine(
                $"[ink-metrics] strokes={_strokeCount} avgPoints={avgStrokePoints:F1} inter={_intermediatePoints} rebuilt={RebuiltStrokeCount} avgTilt={avgTilt:F2}");
            _watch.Restart();
            _strokeCount = 0;
            _strokePointCount = 0;
            _intermediatePoints = 0;
            _tiltSamples = 0;
            _tiltSum = 0;
            RebuiltStrokeCount = 0;
        }
    }
}
