using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI;

/// <summary>
/// Plugin that provides access to data from a specified address.
/// Supports retrieving data, listing available types, and getting schemas.
/// </summary>
public class DataPlugin(IMessageHub hub, IReadOnlyDictionary<string, TypeDescription> typeDefinitionMap, Func<string, Address?> addressMap, JsonSerializerOptions options)
{
    private readonly IWorkspace workspace = hub.GetWorkspace();

    [KernelFunction, Description($"Get data by type name from a specific address. Valid types can be found using the {nameof(GetDataTypes)} function.")]
    public async Task<string> GetData(
        [Description($"Type for which the data should be retrieved. A list of valid data types can be found with the {nameof(GetDataTypes)} function")] string typeName,
        [Description("Optional entity ID. If specified, retrieves a specific entity; otherwise retrieves the entire collection")] string? entityId = null)
    {
        if (!typeDefinitionMap.ContainsKey(typeName))
        {
            return $"Invalid data type: {typeName}. Valid types are: {string.Join(", ", typeDefinitionMap.Keys)}";
        }

        var address = addressMap.Invoke(typeName);

        if (address == null)
            return $"No address defined for type: {typeName}";

        if (!string.IsNullOrEmpty(entityId))
        {
            using var stream = workspace.GetRemoteStream<JsonElement, EntityReference>(address, new EntityReference(typeName, entityId));
            return await stream.Select(x => x.Value.ToString())
                               .FirstAsync();
        }
        else
        {
            using var stream = workspace.GetRemoteStream<JsonElement, CollectionReference>(address, new CollectionReference(typeName));
            return await stream.Select(x => x.Value.ToString()).FirstAsync();
        }
    }

    [KernelFunction, Description($"List all data types available in the {nameof(GetData)} function as a json structure. The name property should be used in the GetData tool.")]
    public async Task<string> GetDataTypes([Description("Optional address to get data types from. If null, returns types from constructor")] string? address = null)
    {
        if (address == null)
            // Return types from constructor
            return JsonSerializer.Serialize(typeDefinitionMap, options);

        // Return types that match the specified address
        var ret = await hub.AwaitResponse<DomainTypesResponse>(new GetDomainTypesRequest(), o => o.WithTarget(address));
        return JsonSerializer.Serialize(ret.Message.Types, options);
    }

    public static string GetTools() =>
        string.Join(", ",
            typeof(DataPlugin)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public).Select(m => m.Name).Where(x => x != nameof(GetTools))
        );

    [KernelFunction, Description($"Gets the JSON schema for a particular type. Use these schemas to validate edited JSON data.")]
    public async Task<string> GetSchema([Description($"Type of the schema to be generated. Use the {nameof(GetDataTypes)} function to get a list of available schemas")] string type, [Description("Address where to retrieve the schema.")]string? address = null)
    {
        var addressType = address is null ? addressMap.Invoke(type) : (Address)address;
        if (addressType is null)
            return $"Unknown type: {type}. Valid types are: {string.Join(", ", typeDefinitionMap.Keys)}";

        var response = await hub.AwaitResponse<SchemaResponse>(new GetSchemaRequest(type), o => o.WithTarget(address));
        return response.Message.Schema ?? $"No schema defined for type '{type}'";
    }

    public KernelPlugin CreateKernelPlugin()
    {
        var plugin = KernelPluginFactory.CreateFromObject(this);
        KernelPluginFactory.CreateFromFunctions(nameof(DataPlugin),
            plugin.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
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
        if(typeDefinitionMap.Any())
            return ret + "\nAvailable types: " + string.Join(", ", typeDefinitionMap.Keys);
        return ret;
    }
}
