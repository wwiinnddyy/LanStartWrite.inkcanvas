using Jalium.UI;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas;

/// <summary>Selection is independent of keyboard focus and pointer-over feedback.</summary>
public sealed class FluentNavigationItem : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(FluentNavigationItem), new PropertyMetadata(false));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty)!;
        set => SetValue(IsSelectedProperty, value);
    }
}
