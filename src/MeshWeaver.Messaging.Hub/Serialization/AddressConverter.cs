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

    private Address ReadFromArray(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0 || parts.Count == 1)
        {
            throw new JsonException("Invalid address format");
        }

        if (parts.Count == 2)
        {
            var addressType = parts[0];
            var id = parts[1];
            return ParseAddress(addressType, id);
        }
        if (parts.Count == 3)
        {
            var addressType = parts[0];
            var id = $"{parts[1]}/{parts[2]}";
            return ParseAddress(addressType, id);
        }

        var host = ReadFromArray(parts.Take(2).ToArray());
        var address = ReadFromArray(parts.Skip(2).ToArray());
        return new HostedAddress(address, host);
    }

    private Address ReadFromString(ref Utf8JsonReader reader)
    {
        var addressString = reader.GetString();
        if (addressString == null)
        {
            throw new JsonException("Address string is null");
        }

        var parts = addressString.Split('/');
        return ReadFromArray(parts);
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
