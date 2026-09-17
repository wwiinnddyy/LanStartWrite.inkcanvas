using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media.Animation;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// A single indicator moves across the pane, outside individual item templates/clips.
/// Adapts NavigationView.cpp::PlayIndicatorAnimations (WinUI 19e3bdc3c):
/// extend toward the destination for 200 ms, then bring the trailing edge in for 400 ms.
/// Jalium animates the shared bar's offset/height rather than two Composition visuals.
/// </summary>
internal sealed class NavigationIndicatorAnimator
{
    internal const double RestingHeight = 16;
    private readonly Border _indicator;
    private bool _positioned;
    private double _targetTop;
    private int _generation;

    internal NavigationIndicatorAnimator(Border indicator)
    {
        _indicator = indicator;
    }

    internal void MoveTo(double left, double top, bool animate)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        Canvas.SetLeft(_indicator, left);

        // LayoutUpdated also runs during the animation. Never restart a trip to the
        // same destination, or an unrelated page layout would continually interrupt it.
        if (_positioned && Math.Abs(top - _targetTop) < 0.01) return;

        var fromTop = Canvas.GetTop(_indicator);
        var fromHeight = _indicator.Height;
        var canAnimate = animate && _positioned;
        _positioned = true;
        _targetTop = top;

        // Read the currently displayed geometry BEFORE detaching previous clocks.
        // Rapid retargeting therefore continues from the visible bar, not the last item.
        Complete();
        _indicator.Opacity = 1;
        if (!canAnimate) return;

        var extendedTop = Math.Min(fromTop, top);
        var extendedBottom = Math.Max(fromTop + fromHeight, top + RestingHeight);
        var offsetAnimation = CreateAnimation(fromTop, extendedTop, top);
        var heightAnimation = CreateAnimation(fromHeight, extendedBottom - extendedTop, RestingHeight);
        var generation = _generation;
        offsetAnimation.Completed += (_, _) =>
        {
            // An old completion must not snap a newer animation to a stale target.
            if (generation == _generation) Complete();
        };
        // Both properties run on the same UIElement animation host. Canvas arranges
        // only this overlay bar, so its motion cannot move or resize navigation items.
        _indicator.BeginAnimation(Canvas.TopProperty, offsetAnimation);
        _indicator.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
    }

    internal void Complete()
    {
        _generation++;
        _indicator.BeginAnimation(Canvas.TopProperty, (AnimationTimeline?)null);
        _indicator.BeginAnimation(FrameworkElement.HeightProperty, (AnimationTimeline?)null);
        Canvas.SetTop(_indicator, _targetTop);
        _indicator.Height = RestingHeight;
    }

    private static DoubleAnimationUsingKeyFrames CreateAnimation(double from, double extended, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(extended,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new KeySpline(0.9, 0.1, 1, 0.2)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), new KeySpline(0.1, 0.9, 0.2, 1)));
        return animation;
    }
}
