using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Patch;

namespace OpenSmc.Data.Serialization;

public class DataChangedEventConverter : JsonConverter<DataChangedEvent>
{
    public override DataChangedEvent Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var address = root.GetProperty("address").Deserialize<object>(options);
        var reference = (WorkspaceReference)
            root.GetProperty("reference").Deserialize<object>(options);
        var version = root.GetProperty("version").GetInt64();
        var changeType = Enum.Parse<ChangeType>(root.GetProperty("changeType").ToString());
        var changedBy = root.TryGetProperty("changedBy", out var prop)
            ? prop.Deserialize<object>(options)
            : null;

        // Deserialize the Change property based on the ChangeType
        var change = changeType switch
        {
            ChangeType.Patch => root.GetProperty("change").Deserialize<JsonPatch>(),
            _ => root.GetProperty("change").Deserialize<object>(options)
        };

        return new DataChangedEvent(version, change, changeType, changedBy)
        {
            Id = address,
            Reference = reference
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        DataChangedEvent value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        writer.WritePropertyName("address");
        JsonSerializer.SerializeToNode(value.Id, value.Id.GetType(), options)!.WriteTo(writer);

        writer.WritePropertyName("reference");
        JsonSerializer
            .SerializeToNode(value.Reference, value.Reference.GetType(), options)!
            .WriteTo(writer);
        writer.WriteNumber("version", value.Version);
        writer.WriteString("changeType", value.ChangeType.ToString());
        if (value.ChangedBy != null)
        {
            writer.WritePropertyName("changedBy");
            JsonSerializer
                .SerializeToNode(value.ChangedBy, value.ChangedBy.GetType(), options)!
                .WriteTo(writer);
        }

        // Serialize the Change property based on the ChangeType
        writer.WritePropertyName("change");
        switch (value.ChangeType)
        {
            case ChangeType.Patch:
                JsonSerializer.SerializeToNode(value.Change, typeof(JsonPatch))!.WriteTo(writer);
                break;
            default:
                JsonSerializer
                    .SerializeToNode(value.Change, value.Change.GetType(), options)!
                    .WriteTo(writer);
                break;
        }
        // Handle other ChangeType values similarly

        writer.WriteEndObject();
    }
}
