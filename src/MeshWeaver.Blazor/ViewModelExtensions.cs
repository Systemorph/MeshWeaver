using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Application.Styles;
using Microsoft.FluentUI.AspNetCore.Components;
using Icon = MeshWeaver.Domain.Icon;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace MeshWeaver.Blazor;

public static class ViewModelExtensions
{
    public static Microsoft.FluentUI.AspNetCore.Components.Icon ToFluentIcon(this Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider =>
                Icons.GetInstance(new IconInfo { Name = icon.Id, Size = IconSize.Size24 }),
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
