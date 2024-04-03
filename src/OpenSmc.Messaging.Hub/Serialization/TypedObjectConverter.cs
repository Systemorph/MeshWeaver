using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class TypedObjectConverter(IServiceProvider serviceProvider) : System.Text.Json.Serialization.JsonConverter<object>
{
    private readonly ISerializationService serializationService =
        serviceProvider.GetRequiredService<ISerializationService>();
    public override bool CanConvert(Type typeToConvert)
        => true;

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            // Convert numeric values to strings
            return reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString(CultureInfo.InvariantCulture);

        if (reader.TokenType == JsonTokenType.String)
            // Keep string values as-is
            return reader.GetString();

        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var rootElement = doc.RootElement;
        return serializationService.Deserialize(rootElement.ToString());
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonNode.Parse(serializationService.SerializeToString(value))?.WriteTo(writer);
    }


}

