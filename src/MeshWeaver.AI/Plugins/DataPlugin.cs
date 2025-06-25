using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin that provides access to data from a specified address.
/// Supports retrieving data, listing available types, and getting schemas.
/// </summary>
public class DataPlugin(
    IMessageHub hub, 
    IAgentChat chat,
    IReadOnlyDictionary<string, TypeDescription>? typeDefinitions = null, 
    Func<string, Address?>? addressMap = null)
{
    private readonly IWorkspace workspace = hub.GetWorkspace();

    [KernelFunction, Description($"Get data by type name from a specific address. Valid types can be found using the {nameof(GetDataTypes)} function.")]
    public async Task<string> GetData(
        [Description($"Type for which the data should be retrieved. A list of valid data types can be found with the {nameof(GetDataTypes)} function")] string type,
        [Description("Optional entity ID. If specified, retrieves a specific entity; otherwise retrieves the entire collection")] string? entityId = null)
    {

        var address = GetAddress(type);

        if (address == null)
            return $"No address defined for type: {type}";

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            using var stream = workspace.GetRemoteStream<JsonElement, EntityReference>(address, new EntityReference(type, entityId));
            return await stream.Select(x => x.Value.ToString())
                               .FirstAsync();
        }
        else
        {
            using var stream = workspace.GetRemoteStream<JsonElement, CollectionReference>(address, new CollectionReference(type));
            return await stream.Select(x => x.Value.ToString()).FirstAsync();
        }
    }

    [KernelFunction, Description($"List all data types available in the {nameof(GetData)} function as a json structure. The name property should be used in the GetData tool.")]
    public async Task<string> GetDataTypes()
    {

        if (typeDefinitions is not null)
            // Return types from constructor
            return JsonSerializer.Serialize(typeDefinitions.Values, hub.JsonSerializerOptions);

        if (chat.Context?.Address is null)
            return "Please navigate to a context for which you want to know the data types.";

        // Return types that match the specified address
        var ret = await hub.AwaitResponse(new GetDomainTypesRequest(), o => o.WithTarget(chat.Context.Address));
        return JsonSerializer.Serialize(ret.Message.Types, hub.JsonSerializerOptions);
    }

    public static string GetTools() =>
        string.Join(", ",
            typeof(DataPlugin)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public).Select(m => m.Name).Where(x => x != nameof(GetTools))
        );

    [KernelFunction, Description($"Gets the JSON schema for a particular type. Use these schemas to map data to JSON or to validate edited JSON data.")]
    public async Task<string> GetSchema([Description($"Type of the schema to be generated. Use the {nameof(GetDataTypes)} function to get a list of available schemas")] string type)
    {
        var address = GetAddress(type);
        if (address is null)
            return $"Unknown type: {type}." + (typeDefinitions is not null ? $" Valid types are: {string.Join(", ", typeDefinitions.Keys)}" : string.Empty);

        var response = await hub.AwaitResponse(new GetSchemaRequest(type), o => o.WithTarget(address));
        return response.Message.Schema ?? $"No schema defined for type '{type}'";
    }

    private Address? GetAddress(string type)
    {
        if (typeDefinitions is not null && typeDefinitions.TryGetValue(type, out var td) && td.Address is not null)
            return td.Address;

        return addressMap?.Invoke(type) ?? chat.Context?.Address;
    }

    public KernelPlugin CreateKernelPlugin()
    {
        var plugin = KernelPluginFactory.CreateFromFunctions(nameof(DataPlugin),
            GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.HasAttribute<KernelFunctionAttribute>())
                .Select(m =>
                    KernelFunctionFactory.CreateFromMethod(m, this,
                        new KernelFunctionFromMethodOptions()
                        {
                            FunctionName = m.Name, Description = CreateDescription(m)
                        })));
        return plugin;
    }

    private string CreateDescription(MethodInfo methodInfo)
    {
        var desc = methodInfo.GetCustomAttribute<DescriptionAttribute>();
        var ret = desc?.Description ?? "";
        if(typeDefinitions?.Any() == true)
            return ret + "\nAvailable types: " + string.Join(", ", typeDefinitions.Keys);
        return ret;
    }
}
