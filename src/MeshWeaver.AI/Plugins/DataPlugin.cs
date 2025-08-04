using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Humanizer;
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

    [KernelFunction, Description($"Get data by type name from a specific address. Valid types can be found using the {nameof(GetDataTypes)} function.")]
    public async Task<string> GetData(
        [Description($"Type for which the data should be retrieved. A list of valid data types can be found with the {nameof(GetDataTypes)} function")] string type,
        [Description("Optional entity ID. If specified, retrieves a specific entity; otherwise retrieves the entire collection")] string? entityId = null)
    {

        var address = GetAddress(type);

        if (address == null)
            return $"No address defined for type: {type}";

        WorkspaceReference reference = !string.IsNullOrWhiteSpace(entityId) 
            ? new EntityReference(type, entityId)
            : new CollectionReference(type);

        var response = await hub.AwaitResponse(new GetDataRequest(reference), o => o.WithTarget(address));
        return JsonSerializer.Serialize(response.Message.Data, hub.JsonSerializerOptions);
    }

    [KernelFunction, Description($"List all data types and their descriptions available in the {nameof(GetData)} function as a json structure. The name property should be used in the GetData tool.")]
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

    [KernelFunction,
     Description(
         $"Updates the data submitted in {nameof(json)} of type {nameof(type)}. The JSON schema as provided in GetSchema with this type has to be fulfilled.")]
    public async Task<string> UpdateData(
        [Description("Json representation of the entity, has to conform to the schema as found in the GetSchema tool.")] string json,
        [Description("Name of the type to be updated. Must be in list of available types.")] string type
    )
    {
        var address = GetAddress(type);

        if (address == null)
            return $"No address defined for type: {type}";

        var response = await hub.AwaitResponse(new DataChangeRequest() { Updates = [JsonDocument.Parse(json).RootElement] }, o => o.WithTarget(address));

        if (response.Message.Log.Status == ActivityStatus.Succeeded)
            return $"Data of type '{type}' updated successfully.";
        return $"Failed to update data of type '{type}': {response.Message}";
    }
    [KernelFunction,
     Description(
         $"Deletes the data submitted in {nameof(json)} of type {nameof(type)}. The JSON schema as provided in GetSchema with this type has to be fulfilled.")]
    public async Task<string> DeleteData(
        [Description("Json representation of the entity, has to conform to the schema as found in the GetSchema tool.")] string json,
        [Description("Name of the type to be deleted. Must be in list of available types.")] string type
    )
    {
        var address = GetAddress(type);

        if (address == null)
            return $"No address defined for type: {type}";

        var response = await hub.AwaitResponse(new DataChangeRequest() { Deletions = [JsonDocument.Parse(json).RootElement] }, o => o.WithTarget(address));

        if (response.Message.Log.Status == ActivityStatus.Succeeded)
            return $"Data of type '{type}' updated successfully.";
        return $"Failed to update data of type '{type}': {response.Message}";
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
        try
        {
            var response = await hub.AwaitResponse(new GetSchemaRequest(type), o => o.WithTarget(address),
                new CancellationTokenSource(10.Seconds()).Token);
            return response.Message.Schema;
        }
        catch 
        {
            return $"Type {type} was not found in Address {address}";
        }
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
                {
                    var ret = KernelFunctionFactory.CreateFromMethod(m, hub.JsonSerializerOptions, this);
                    if (ret.Name == nameof(GetData))
                    {
                        var typeParameter = ret.Metadata.Parameters.First();
                        typeParameter.Description = EnrichDescriptionByTypes(typeParameter.Description);
                    }
                    return ret;
                })
                .ToList()
            );
       return plugin;
    }

    private string EnrichDescriptionByTypes(string description)
    {
        if (typeDefinitions?.Any() == true)
            return description + "\nAvailable types: " + string.Join(", ", typeDefinitions.Keys);
        return description;
    }

}
