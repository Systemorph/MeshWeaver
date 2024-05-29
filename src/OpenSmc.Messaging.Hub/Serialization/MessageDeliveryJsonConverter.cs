using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class MessageDeliveryRawJsonConverter : JsonConverter<MessageDelivery<RawJson>>
{
    public override MessageDelivery<RawJson> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, MessageDelivery<RawJson> value, JsonSerializerOptions options)
        => throw new NotImplementedException();
}
