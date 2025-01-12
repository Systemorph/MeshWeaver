using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

public class AddressConverter(Dictionary<string, Type> addressTypes) : JsonConverter<Address>
{
    public override Address Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var addressString = reader.GetString();
        if (addressString == null)
        {
            throw new JsonException("Address string is null");
        }

        var parts = addressString.Split('/');
        if (parts.Length == 0)
        {
            throw new JsonException("Invalid address format");
        }

        var addressType = parts[0];
        var id = parts.Length > 1 ? string.Join('/', parts, 1, parts.Length - 1) : string.Empty;

        if (!addressTypes.TryGetValue(addressType, out var concreteType))
        {
            throw new JsonException($"Unknown address type: {addressType}");
        }

        var json = $"{{\"Id\":\"{id}\"}}";
        var address = (Address)JsonSerializer.Deserialize(json, concreteType);
        return address;
    }

    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
