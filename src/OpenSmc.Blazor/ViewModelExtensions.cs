using JustifyContent = OpenSmc.Layout.JustifyContent;
using FluentJustifyContent = Microsoft.FluentUI.AspNetCore.Components.JustifyContent;

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
}
