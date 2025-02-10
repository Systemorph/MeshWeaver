using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

public class AddressConverter(ITypeRegistry typeRegistry) : JsonConverter<Address>
{
    public override Address Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.StartObject => ReadFromObject(reader),
            JsonTokenType.String => ReadFromString(reader),
            _ => throw new JsonException("Unexpected token type")
        };
    }

    private Address ReadFromObject(Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (
            root.TryGetProperty("type", out var typeElement) &&
            root.TryGetProperty("id", out var idElement)
        )
            return ParseAddress(typeElement.GetString(), idElement.GetString());
        else
            throw new JsonException("Invalid address object format");
    }

    private Address ReadFromString(Utf8JsonReader reader)
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

        return ParseAddress(addressType, id);
    }

    private Address ParseAddress(string addressType, string id)
    {
        if (!typeRegistry.TryGetType(addressType, out var concreteType))
        {
            return new Address(addressType, id);
            //throw new JsonException($"Unknown address type: {addressType}");
        }

        var json = $"{{\"Id\":\"{id}\"}}";
        var address = Activator.CreateInstance(concreteType.Type, [id]);
        return (Address)address;
    }

    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
