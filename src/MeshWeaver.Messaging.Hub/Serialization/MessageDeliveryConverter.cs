using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeshWeaver.Utils;

namespace MeshWeaver.Messaging.Serialization;

public class MessageDeliveryConverter : JsonConverter<object>
{
    public override bool CanConvert(Type typeToConvert) => typeof(IMessageDelivery).IsAssignableFrom(typeToConvert);

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }


    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (value == null)
            return;

        var delivery = (IMessageDelivery)value;
        var clonedOptions = options.CloneAndRemove(this);
        var serialized = (JsonObject)JsonSerializer.SerializeToNode(delivery, clonedOptions)!;
        if (delivery.Target != null && delivery.Target is not JsonNode && serialized.TryGetPropertyValue(nameof(IMessageDelivery.Target).ToCamelCase(), out var serializedTarget))
        {
            serializedTarget![EntitySerializationExtensions.IdProperty] = delivery.Target.ToString();
        }
        if (delivery.Sender != null && delivery.Sender is not JsonNode && serialized.TryGetPropertyValue(nameof(IMessageDelivery.Sender).ToCamelCase(), out var serializedSender))
        {
            serializedSender![EntitySerializationExtensions.IdProperty] = delivery.Sender.ToString();
        }

        serialized.WriteTo(writer);
    }
}
