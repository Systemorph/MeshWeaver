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
    /// <summary>
    /// Reads an <see cref="Address"/> from JSON, accepting either a string token
    /// ("type/seg1/seg2", with an optional "@" host suffix) or an object token
    /// ({ "type", "id", "host" }) for backward compatibility.
    /// </summary>
    /// <param name="reader">The reader positioned at the address token.</param>
    /// <param name="typeToConvert">The target type (always <see cref="Address"/>).</param>
    /// <param name="options">The active serializer options.</param>
    /// <returns>The deserialized <see cref="Address"/>.</returns>
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

        // Build segments array: type + id segments
        var idSegments = string.IsNullOrEmpty(id) ? [] : id.Split('/');
        var allSegments = new[] { type }.Concat(idSegments).ToArray();
        var address = new Address(allSegments);

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

    private static Address ParseSimple(string address) =>
        new(address.Split('/'));

    /// <summary>
    /// Writes an <see cref="Address"/> as a single string value using its full
    /// string form (which includes the host segment when one is present).
    /// </summary>
    /// <param name="writer">The writer to emit the address to.</param>
    /// <param name="value">The address to serialize.</param>
    /// <param name="options">The active serializer options.</param>
    public override void Write(Utf8JsonWriter writer, Address value, JsonSerializerOptions options)
    {
        // Use ToFullString to include host if present
        writer.WriteStringValue(value.ToFullString());
    }
}
