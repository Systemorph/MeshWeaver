using Microsoft.FluentUI.AspNetCore.Components;
using OpenSmc.Application.Styles;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace OpenSmc.Blazor;

public static class ViewModelExtensions
{
    public static JustifyContent ToFluentJustifyContent(this Layout.JustifyContent justify) =>
        justify switch
        {
            Layout.JustifyContent.FlexStart => JustifyContent.FlexStart,
            Layout.JustifyContent.Center => JustifyContent.Center,
            Layout.JustifyContent.FlexEnd => JustifyContent.FlexEnd,
            Layout.JustifyContent.SpaceBetween => JustifyContent.SpaceBetween,
            Layout.JustifyContent.SpaceAround => JustifyContent.SpaceAround,
            Layout.JustifyContent.SpaceEvenly => JustifyContent.SpaceEvenly,
            _ => JustifyContent.FlexStart
        };

    public static Microsoft.FluentUI.AspNetCore.Components.Icon ToFluentIcon(this Application.Styles.Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider =>
                Icons.GetInstance(new IconInfo { Name = icon.Id, Size = IconSize.Size24 }),
            _ => null
        };

    public static Orientation ToFluentOrientation(this Layout.Orientation orientation) => 
        orientation switch
        {
            Layout.Orientation.Horizontal => Orientation.Horizontal,
            Layout.Orientation.Vertical => Orientation.Vertical,
            _ => Orientation.Horizontal
        };

}
