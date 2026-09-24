using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;

namespace LanStartWrite.Inkcanvas;

/// <summary>
/// 具备 Radio 语义：<see cref="ToggleButton"/> 在已选中时再次点击不会取消选中，
/// 而是触发 <see cref="Reactivated"/>（用于笔工具的二级菜单等）。
/// </summary>
public sealed class RadioToolToggleButton : ToggleButton
{
    public static readonly RoutedEvent ReactivatedEvent = EventManager.RegisterRoutedEvent(
        nameof(Reactivated),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(RadioToolToggleButton));

    public event RoutedEventHandler Reactivated
    {
        add => AddHandler(ReactivatedEvent, value);
        remove => RemoveHandler(ReactivatedEvent, value);
    }

    protected override void OnToggle()
    {
        if (IsChecked == true)
        {
            RaiseEvent(new RoutedEventArgs(ReactivatedEvent, this));
            return;
        }

        base.OnToggle();
    }
}
