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
            JsonTokenType.StartObject => ReadFromObject(ref reader),
            JsonTokenType.String => ReadFromString(ref reader),
            _ => throw new JsonException("Unexpected token type")
        };
    }

    private Address ReadFromObject(ref Utf8JsonReader reader)
    {
        string? type = null;
        string? id = null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                string propertyName = reader.GetString()!;
                reader.Read();
                switch (propertyName)
                {
                    case "type":
                        type = reader.GetString();
                        break;
                    case "id":
                        id = reader.GetString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        if (type == null || id == null)
            throw new JsonException("Invalid address object format");
        return ParseAddress(type, id);
    }

    private Address ReadFromString(ref Utf8JsonReader reader)
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
        }

        var address = (Address)Activator.CreateInstance(concreteType!.Type, [id])!;
        return address;
    }

    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
