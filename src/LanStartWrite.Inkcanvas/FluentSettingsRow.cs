using Jalium.UI;
using Jalium.UI.Controls;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// A settings row with a description followed by an action. At narrow content widths
/// the action moves below the description instead of squeezing it into a few glyphs.
/// The children stay in the same visual tree, preserving focus and bindings.
/// </summary>
public sealed class FluentSettingsRow : Grid
{
    public static readonly DependencyProperty StackAtWidthProperty = DependencyProperty.Register(
        nameof(StackAtWidth), typeof(double), typeof(FluentSettingsRow),
        new FrameworkPropertyMetadata(360d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double StackAtWidth
    {
        get => (double)GetValue(StackAtWidthProperty)!;
        set => SetValue(StackAtWidthProperty, value);
    }

    public bool IsStacked { get; private set; }
    private bool _arranged;

    public FluentSettingsRow()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var stacked = double.IsFinite(availableSize.Width) && availableSize.Width < StackAtWidth;
        if (Children.Count == 2 && Children[1] is FrameworkElement action && (!_arranged || stacked != IsStacked))
        {
            _arranged = true;
            IsStacked = stacked;
            SetColumn(action, stacked ? 0 : 1);
            SetRow(action, stacked ? 1 : 0);
            SetColumnSpan(action, stacked ? 2 : 1);
            SetColumnSpan(Children[0], stacked ? 2 : 1);
            action.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
            action.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        return base.MeasureOverride(availableSize);
    }
}
