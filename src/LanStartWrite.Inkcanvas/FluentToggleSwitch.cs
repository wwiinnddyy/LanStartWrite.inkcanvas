using Jalium.UI;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Input;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// A ToggleButton-backed WinUI-size switch. Keeps Jalium's Toggle automation/keyboard
/// contract; implements independent mouse and per-contact touch drag without mouse promotion.
/// The stock Jalium ToggleSwitch has hard-coded 44px spring geometry, so it cannot host
/// the source's 40px track and 12/14/17px knob accurately merely by replacing its template.
/// </summary>
public sealed class FluentToggleSwitch : ToggleButton
{
    public static readonly DependencyProperty ThumbOffsetProperty = DependencyProperty.Register(
        nameof(ThumbOffset), typeof(Thickness), typeof(FluentToggleSwitch), new PropertyMetadata(new Thickness(0)));
    public static readonly DependencyProperty IsPointerPressedProperty = DependencyProperty.Register(
        nameof(IsPointerPressed), typeof(bool), typeof(FluentToggleSwitch), new PropertyMetadata(false));

    public Thickness ThumbOffset { get => (Thickness)GetValue(ThumbOffsetProperty)!; private set => SetValue(ThumbOffsetProperty, value); }
    public bool IsPointerPressed { get => (bool)GetValue(IsPointerPressedProperty)!; private set => SetValue(IsPointerPressedProperty, value); }
    private TouchDevice? _touch;
    private bool _mouse;
    private double _startX;
    private double _startOffset;
    private bool _dragged;
    private bool? _gestureValue;

    public FluentToggleSwitch()
    {
        IsThreeState = false;
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => { if (_mouse) Cancel(); };
        PreviewTouchDown += OnTouchDown;
        PreviewTouchMove += OnTouchMove;
        PreviewTouchUp += OnTouchUp;
        LostTouchCapture += (_, e) => { if (_touch?.Id == e.TouchDevice.Id) Cancel(); };
        PreviewPointerCancel += (_, e) =>
        {
            if (e is PointerEventArgs pointer && _touch?.Id == pointer.Pointer.PointerId)
                Cancel();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsPointerPressed) { Cancel(); e.Handled = true; }
        };
        Unloaded += (_, _) => Cancel();
    }

    protected override void OnChecked(RoutedEventArgs e) { Snap(); base.OnChecked(e); }
    protected override void OnUnchecked(RoutedEventArgs e) { Snap(); base.OnUnchecked(e); }
    protected override void OnToggle()
    {
        // Gesture commits still pass through ButtonBase.OnClick so Click/Command and
        // keyboard activation share one contract. Dragging must not toggle a second time.
        if (_gestureValue is bool value) SetCurrentValue(IsCheckedProperty, value);
        else base.OnToggle();
    }
    public override void OnApplyTemplate() { base.OnApplyTemplate(); Snap(); }
    protected override void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        base.OnIsEnabledChanged(oldValue, newValue);
        if (!newValue) Cancel();
    }

    private void Begin(double x)
    {
        _startX = x;
        _startOffset = IsChecked == true ? 20 : 0;
        _dragged = false;
        IsPointerPressed = true;
        Focus();
    }

    private void Move(double x)
    {
        var delta = x - _startX;
        _dragged |= Math.Abs(delta) >= 3;
        if (_dragged) ThumbOffset = new Thickness(Math.Clamp(_startOffset + delta, 0, 20), 0, 0, 0);
    }

    private void End(Point point)
    {
        var commit = _dragged || (point.X >= 0 && point.X <= ActualWidth && point.Y >= 0 && point.Y <= ActualHeight);
        var next = _dragged ? ThumbOffset.Left >= 10 : IsChecked != true;
        Release();
        try
        {
            if (commit)
            {
                _gestureValue = next;
                OnClick();
            }
        }
        finally
        {
            _gestureValue = null;
            Snap();
        }
    }

    private void Snap() => ThumbOffset = new Thickness(IsChecked == true ? 20 : 0, 0, 0, 0);
    private void Cancel() { Release(); Snap(); }
    private void Release()
    {
        var touch = _touch;
        var mouse = _mouse;
        _touch = null;
        _mouse = false;
        IsPointerPressed = false;
        if (touch is not null) ReleaseTouchCapture(touch);
        if (mouse && IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled || _touch is not null || _mouse) return;
        if (!CaptureMouse()) return;
        _mouse = true;
        Begin(e.GetPosition(this).X);
        e.Handled = true;
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouse) return;
        Move(e.GetPosition(this).X);
        e.Handled = true;
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouse) return;
        End(e.GetPosition(this));
        e.Handled = true;
    }
    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (!IsEnabled) return;
        e.Handled = true;
        if (_touch is not null || _mouse || !CaptureTouch(e.TouchDevice)) return;
        _touch = e.TouchDevice;
        Begin(e.GetTouchPoint(this).Position.X);
    }
    private void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (_touch?.Id != e.TouchDevice.Id) return;
        Move(e.GetTouchPoint(this).Position.X);
        e.Handled = true;
    }
    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (_touch?.Id != e.TouchDevice.Id) return;
        End(e.GetTouchPoint(this).Position);
        e.Handled = true;
    }
}
