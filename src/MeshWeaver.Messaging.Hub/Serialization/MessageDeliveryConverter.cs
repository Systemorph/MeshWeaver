using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeshWeaver.Utils;

namespace MeshWeaver.Messaging.Serialization;

public class MessageDeliveryConverter() : JsonConverter<object>
{
    public override bool CanConvert(Type typeToConvert) => typeof(IMessageDelivery).IsAssignableFrom(typeToConvert);

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
        //var jsonObject = JsonSerializer.Deserialize<JsonObject>(ref reader, options);
        //if (jsonObject == null)
        //    throw new JsonException("Failed to deserialize JSON object.");

        //var typeName = jsonObject[EntitySerializationExtensions.TypeProperty]?.ToString();
        //if (string.IsNullOrEmpty(typeName))
        //    throw new JsonException("Type of message not specified");

        //var type = 


        //var message = jsonObject["message"].Deserialize<object>(options);
        //if (message == null)
        //    throw new JsonException($"No message was contained in the delivery: {jsonObject}");


        //var messageType = message.GetType();

        //var sender = jsonObject["sender"];
        //if (sender is null)
        //    throw new JsonException("No sender specified.");
        //var target = jsonObject["target"];
        //if (target is null)
        //    throw new JsonException("No target specified.");

        //var delivery = (IMessageDelivery)Activator.CreateInstance(
        //    typeof(MessageDelivery<>).MakeGenericType(messageType), 
        //    sender.Deserialize<object>(options),
        //    target.Deserialize<object>(options),
        //    message)!;

        //var properties = jsonObject["properties"];
        //if(properties is not null)
        //    delivery = delivery.SetProperties(properties.Deserialize<Dictionary<string, object>>(options));
        //return delivery;
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
