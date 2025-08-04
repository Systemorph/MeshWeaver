using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin that provides access to layout areas from a specified address.
/// Supports retrieving layout area definitions and listing available layout areas.
/// </summary>
public class LayoutAreaPlugin(
    IMessageHub hub, 
    IAgentChat chat,
    IReadOnlyDictionary<string, LayoutAreaDefinition>? areaDefinitions = null, 
    Func<string, Address?>? addressMap = null,
    Address? defaultAddress = null
    )
{

    [KernelFunction, 
     Description($"Displays a layout area. Return the string from this function as is without any additional text. It should start with @")]
    public string DisplayLayoutArea(
        [Description($"Name of the layout area to retrieve. A list of valid layout areas can be found with the {nameof(GetLayoutAreas)} function")] string areaName,
        [Description($"Id of the layout area requested. Could be paramters steering the layout areas. See Layout Area Definition for details.")] string? id = null)
    {

        if (areaDefinitions?.TryGetValue(areaName, out var definition) == true)
            return $"@{definition.Url}";

        var address = GetAddress(areaName);

        if (address == null)
            return $"No address defined for layout area: {areaName}";

        var ret = $"@{address}/{areaName}";
        if (id is not null && id != "null")
            ret = $"{ret}/{id}";

        return ret;
    }

    [KernelFunction, 
     Description($"List all layout areas and their definitions available as a json structure. The area property should be used in the {nameof(DisplayLayoutArea)} tool.")]
    public async Task<string> GetLayoutAreas()
    {

        if (areaDefinitions is not null)
            // Return areas from constructor
            return JsonSerializer.Serialize(areaDefinitions.Values, hub.JsonSerializerOptions);

        if (chat.Context?.Address is null)
            return "Please navigate to a context for which you want to know the layout areas.";

        // Return areas that match the specified address
        var ret = await hub.AwaitResponse(new GetLayoutAreasRequest(), o => o.WithTarget(chat.Context.Address));
        return JsonSerializer.Serialize(ret.Message.Areas, hub.JsonSerializerOptions);
    }

    public static string GetTools() =>
        string.Join(", ",
            typeof(LayoutAreaPlugin)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public).Select(m => m.Name).Where(x => x != nameof(GetTools))
        );

    private Address? GetAddress(string areaName)
    {
        if (areaDefinitions is not null && areaDefinitions.TryGetValue(areaName, out var area))
            // For now, we don't have address in LayoutAreaDefinition, so fall back to chat context
            return chat.Context?.Address;

        return addressMap?.Invoke(areaName) ?? chat.Context?.Address;
    }

    public KernelPlugin CreateKernelPlugin()
    {
        var plugin = KernelPluginFactory.CreateFromFunctions(nameof(LayoutAreaPlugin),
            GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.HasAttribute<KernelFunctionAttribute>())
                .Select(m =>
                {
                    var ret = KernelFunctionFactory.CreateFromMethod(m, hub.JsonSerializerOptions, this);
                    if (ret.Name == nameof(DisplayLayoutArea))
                    {
                        var areaParameter = ret.Metadata.Parameters.First();
                        areaParameter.Description = EnrichDescriptionByAreas(areaParameter.Description);
                    }
                    return ret;
                })
                .ToList()
            );
       return plugin;
    }

    private string EnrichDescriptionByAreas(string description)
    {
        if (areaDefinitions?.Any() == true)
            return description + "\nAvailable layout areas: " + string.Join(", ", areaDefinitions.Keys);
        return description;
    }

}
