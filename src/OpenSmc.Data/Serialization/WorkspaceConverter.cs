using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;
using OpenSmc.Messaging;

namespace OpenSmc.Data.Serialization;

public class WorkspaceConverter(IMessageHub hub) : JsonConverter<WorkspaceState>
{
    public override WorkspaceState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return Deserialize(doc.RootElement.AsNode(), options);
    }

    public override void Write(Utf8JsonWriter writer, WorkspaceState value, JsonSerializerOptions options)
    {
        Serialize(value, options).WriteTo(writer);
    }

    private JsonNode Serialize(WorkspaceState workspace, JsonSerializerOptions options)
    {
        return new JsonObject
        {
            ["store"] = JsonSerializer.SerializeToNode(workspace.Store, typeof(EntityStore), options),
            ["$type"] = typeof(WorkspaceState).FullName
        };
    }

    public WorkspaceState Deserialize(JsonNode serializedWorkspace, JsonSerializerOptions options)
    {
        if (serializedWorkspace is not JsonObject obj)
            throw new ArgumentException("Invalid serialized workspace");


        if(!obj.TryGetPropertyValue("store", out var storeSerialized))
            throw new ArgumentException("Invalid serialized workspace. No store property set.");
        return hub.GetWorkspace().CreateState(storeSerialized.Deserialize<EntityStore>());
    }

}