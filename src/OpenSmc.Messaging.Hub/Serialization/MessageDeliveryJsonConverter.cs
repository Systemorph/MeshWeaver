//using System.Text.Json;
//using System.Text.Json.Nodes;
//using System.Text.Json.Serialization;

//namespace OpenSmc.Messaging.Serialization;

//public class MessageDeliveryRawJsonConverter : JsonConverter<MessageDelivery<RawJson>>
//{
//    private const string TypeProperty = "$type";

//    public override MessageDelivery<RawJson> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
//    {
//        var node = JsonNode.Parse(ref reader);

//        var content = node["message"].ToJsonString();
//        var rawJson = new RawJson(content);
//        var sender = node["sender"].Deserialize<object>(options);
//        var target = node["target"].Deserialize<object>(options);
//        var properties = node["properties"]?.Deserialize<Dictionary<string, object>>(options) ?? [];
//        var state = node["state"]?.GetValue<string>();
//        var id = node["id"]?.GetValue<string>();

//        var postOptions =
//            new PostOptions(sender, null)
//                .WithTarget(target)
//                .WithProperties(properties);
//        return string.IsNullOrWhiteSpace(id)
//            ? string.IsNullOrWhiteSpace(state)
//                ? new MessageDelivery<RawJson>(rawJson, postOptions)
//                : new MessageDelivery<RawJson>(rawJson, postOptions)
//                    {
//                        State = state,
//                    }
//            : string.IsNullOrWhiteSpace(state)
//                ? new MessageDelivery<RawJson>(rawJson, postOptions)
//                    {
//                        Id = id,
//                    }
//                : new MessageDelivery<RawJson>(rawJson, postOptions)
//                    {
//                        State = state,
//                        Id = id,
//                    };
//    }

//    public override void Write(Utf8JsonWriter writer, MessageDelivery<RawJson> value, JsonSerializerOptions options)
//    {
//        string internalTypeName = null;
//        writer.WriteStartObject();
//        writer.WritePropertyName("message");
//        if (string.IsNullOrWhiteSpace(value.Message?.Content))
//        {
//            writer.WriteNullValue();
//        }
//        else
//        {
//            using var doc = JsonDocument.Parse(value.Message.Content);
//            var root = doc.RootElement;
//            if (root.TryGetProperty(TypeProperty, out var typeElement))
//                internalTypeName = typeElement.GetString();

//            writer.WriteRawValue(value.Message.Content);
//        }

//        writer.WritePropertyName("sender");
//        JsonSerializer.SerializeToNode(value.Sender, value.Sender.GetType(), options)!.WriteTo(writer);
//        writer.WritePropertyName("target");
//        JsonSerializer.SerializeToNode(value.Target, value.Target.GetType(), options)!.WriteTo(writer);
//        writer.WritePropertyName("properties");
//        JsonSerializer.SerializeToNode(value.Properties, value.Properties.GetType(), options)!.WriteTo(writer);
//        writer.WritePropertyName("state");
//        writer.WriteStringValue(value.State);
//        writer.WritePropertyName("id");
//        writer.WriteStringValue(value.Id);

//        if (!string.IsNullOrWhiteSpace(internalTypeName))
//        {
//            var dispatchedTypeName = TypeRegistry.FormatType(typeof(MessageDelivery<>), _ => [internalTypeName]);
//            writer.WritePropertyName(TypeProperty);
//            writer.WriteStringValue(dispatchedTypeName);
//        }

//        writer.WriteEndObject();
//    }
//}
