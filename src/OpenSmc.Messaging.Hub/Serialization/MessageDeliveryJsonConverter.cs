using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class MessageDeliveryRawJsonConverter : JsonConverter<MessageDelivery<RawJson>>
{
    public override MessageDelivery<RawJson> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);

        var content = node["message"].ToJsonString();
        var rawJson = new RawJson(content);
        var sender = node["sender"].Deserialize<object>(options);
        var target = node["target"].Deserialize<object>(options);
        var properties = node["properties"]?.Deserialize<Dictionary<string, object>>(options) ?? [];
        var state = node["state"]?.GetValue<string>();
        var id = node["id"]?.GetValue<string>();

        var postOptions =
            new PostOptions(sender, null)
                .WithTarget(target)
                .WithProperties(properties);
        return string.IsNullOrWhiteSpace(id)
            ? string.IsNullOrWhiteSpace(state)
                ? new MessageDelivery<RawJson>(rawJson, postOptions)
                : new MessageDelivery<RawJson>(rawJson, postOptions)
                    {
                        State = state,
                    }
            : string.IsNullOrWhiteSpace(state)
                ? new MessageDelivery<RawJson>(rawJson, postOptions)
                    {
                        Id = id,
                    }
                : new MessageDelivery<RawJson>(rawJson, postOptions)
                    {
                        State = state,
                        Id = id,
                    };
    }

    public override void Write(Utf8JsonWriter writer, MessageDelivery<RawJson> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("message");
        if (string.IsNullOrWhiteSpace(value.Message?.Content))
            writer.WriteNullValue();
        else
            writer.WriteRawValue(value.Message.Content);

        writer.WritePropertyName("sender");
        JsonSerializer.SerializeToNode(value.Sender, value.Sender.GetType(), options)!.WriteTo(writer);
        writer.WritePropertyName("target");
        JsonSerializer.SerializeToNode(value.Target, value.Target.GetType(), options)!.WriteTo(writer);
        writer.WritePropertyName("properties");
        JsonSerializer.SerializeToNode(value.Properties, value.Properties.GetType(), options)!.WriteTo(writer);
        writer.WritePropertyName("state");
        writer.WriteStringValue(value.State);
        writer.WritePropertyName("id");
        writer.WriteStringValue(value.Id);

        writer.WriteEndObject();
    }
}
