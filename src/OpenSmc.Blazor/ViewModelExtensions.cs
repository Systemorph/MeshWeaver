using Microsoft.FluentUI.AspNetCore.Components;
using OpenSmc.Application.Styles;
using JustifyContent = OpenSmc.Layout.JustifyContent;
using FluentJustifyContent = Microsoft.FluentUI.AspNetCore.Components.JustifyContent;
using Icon = Microsoft.FluentUI.AspNetCore.Components.Icon;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace OpenSmc.Blazor;

public static class ViewModelExtensions
{
    public static FluentJustifyContent ToFluentJustifyContent(this JustifyContent justify) =>
        justify switch
        {
            JustifyContent.FlexStart => FluentJustifyContent.FlexStart,
            JustifyContent.Center => FluentJustifyContent.Center,
            JustifyContent.FlexEnd => FluentJustifyContent.FlexEnd,
            JustifyContent.SpaceBetween => FluentJustifyContent.SpaceBetween,
            JustifyContent.SpaceAround => FluentJustifyContent.SpaceAround,
            JustifyContent.SpaceEvenly => FluentJustifyContent.SpaceEvenly,
            _ => FluentJustifyContent.FlexStart
        };

    public static Icon ToFluentIcon(this Application.Styles.Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider =>
                Icons.GetInstance(new IconInfo { Name = icon.Id, Size = IconSize.Size24 }),
            _ => null
        };
}
