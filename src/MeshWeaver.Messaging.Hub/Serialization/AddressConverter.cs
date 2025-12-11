using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// JSON converter for Address that handles both string and object formats.
/// String format: "type/seg1/seg2" or "host-type/host-seg@inner-type/inner-seg" for hosted addresses.
/// Object format: { "type": "...", "id": "..." } for backward compatibility.
/// </summary>
public class AddressConverter : JsonConverter<Address>
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

    private static Address ReadFromObject(ref Utf8JsonReader reader)
    {
        string? type = null;
        string? id = null;
        string? host = null;
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
                    case "host":
                        host = reader.GetString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        if (type == null)
            throw new JsonException("Invalid address object format: missing type");

        // Parse segments from id (can be empty)
        var segments = string.IsNullOrEmpty(id) ? [] : id.Split('/');
        var address = new Address(type, segments);

        // If host is present, parse and attach it
        if (!string.IsNullOrEmpty(host))
        {
            address = address with { Host = ParseSimple(host) };
        }

        return address;
    }

    private static Address ReadFromString(ref Utf8JsonReader reader)
    {
        var addressString = reader.GetString();
        if (addressString == null)
        {
            throw new JsonException("Address string is null");
        }

        // Use the implicit operator which handles @ separator
        return addressString;
    }

    private static Address ParseSimple(string address)
    {
        var parts = address.Split('/');
        return new Address(parts[0], parts.Length > 1 ? parts[1..] : []);
    }

    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        // Use ToFullString to include host if present
        writer.WriteStringValue(value.ToFullString());
    }
}
