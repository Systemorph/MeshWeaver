using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Application.Styles;
using Microsoft.FluentUI.AspNetCore.Components;
using Icon = MeshWeaver.Domain.Icon;
using MeshWeaver.Data;

namespace MeshWeaver.Blazor;

public static class ViewModelExtensions
{
    public static Microsoft.FluentUI.AspNetCore.Components.Icon ToFluentIcon(this Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider =>
                new IconInfo { Name = icon.Id, Size = (IconSize)icon.Size, Variant = (IconVariant)icon.Variant}.GetInstance(),
            _ => null
        };


    internal static  UiControl GetControl(this ISynchronizationStream<JsonElement> stream, ChangeItem<JsonElement> item, string area)
    {
        return item.Value.TryGetProperty(LayoutAreaReference.Areas, out var controls)
               && controls.TryGetProperty(JsonSerializer.Serialize(area), out var node)
            ? node.Deserialize<UiControl>(stream.Hub.JsonSerializerOptions)
            : null;
    }


    internal static string GetArea(this NamedAreaControl control)
        => control.Area.ToString();

}
