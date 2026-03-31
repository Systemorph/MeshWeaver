#nullable enable
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Application.Styles;
using Microsoft.FluentUI.AspNetCore.Components;
using Icon = MeshWeaver.Domain.Icon;
using MeshWeaver.Data;

namespace MeshWeaver.Blazor;

public static class ViewModelExtensions
{
    public static Microsoft.FluentUI.AspNetCore.Components.Icon? ToFluentIcon(this Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider =>
                new IconInfo { Name = icon.Id, Size = (IconSize)icon.Size, Variant = (IconVariant)icon.Variant }.GetInstance(),
            CustomIcons.Provider => CreateCustomIcon(icon),
            _ => null
        };

    private static Microsoft.FluentUI.AspNetCore.Components.Icon? CreateCustomIcon(Icon icon)
    {
        var svgContent = CustomIcons.GetSvgContent(icon.Id);
        if (string.IsNullOrEmpty(svgContent))
            return null;

        // Extract the path/content from SVG for FluentUI Icon
        // FluentUI Icon expects SVG inner content (path, rect, etc.)
        return new Microsoft.FluentUI.AspNetCore.Components.Icon(
            icon.Id,
            IconVariant.Regular,
            IconSize.Size20,
            svgContent);
    }


    internal static UiControl? GetControl(this ISynchronizationStream<JsonElement> stream, ChangeItem<JsonElement> item, string area)
    {
        return item.Value.TryGetProperty(LayoutAreaReference.Areas, out var controls)
               && controls.TryGetProperty(JsonSerializer.Serialize(area), out var node)
            ? node.Deserialize<UiControl>(stream.Hub.JsonSerializerOptions)
            : null;
    }


    internal static string GetArea(this NamedAreaControl control)
        => control.Area?.ToString() ?? string.Empty;

}
