using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI;

/// <summary>
/// Plugin that provides access to data from a specified address.
/// Supports retrieving data, listing available types, and getting schemas.
/// </summary>
public class DataPlugin(IMessageHub hub, IReadOnlyDictionary<string, TypeDescription> typeDefinitionMap)
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

        var typeDefinition = typeDefinitionMap[typeName];
        var address = typeDefinition.Address;

        if (address == null)
        {
            return $"No address defined for type: {typeName}";
        }

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

    [KernelFunction, Description($"List all data types available in the {nameof(GetData)} function")]
    public IEnumerable<string> GetDataTypes([Description("Optional address to get data types from. If null, returns types from constructor")] Address? address = null)
    {
        if (address == null)
        {
            // Return types from constructor
            return typeDefinitionMap.Keys;
        }

        // Return types that match the specified address
        return typeDefinitionMap.Where(kvp => kvp.Value.Address?.Equals(address) == true)
                                .Select(kvp => kvp.Key);
    }

    public static string GetTools() =>
        string.Join(", ",
            typeof(DataPlugin)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public).Select(m => m.Name).Where(x => x != nameof(GetTools))
        );

    [KernelFunction, Description($"Gets the JSON schema for a particular type. Use these schemas to validate edited JSON data.")]
    public async Task<string> GetSchema([Description($"Type of the schema to be generated. Use the {nameof(GetDataTypes)} function to get a list of available schemas")] string type)
    {
        if (!typeDefinitionMap.ContainsKey(type))
        {
            return $"Unknown type: {type}. Valid types are: {string.Join(", ", typeDefinitionMap.Keys)}";
        }

        var typeDefinition = typeDefinitionMap[type];
        var address = typeDefinition.Address;

        if (address == null)
        {
            return $"No address defined for type: {type}";
        }

        var response = await hub.AwaitResponse<SchemaResponse>(new GetSchemaRequest(type), o => o.WithTarget(address));
        return response.Message.Schema ?? $"No schema defined for type '{type}'";
    }
}
