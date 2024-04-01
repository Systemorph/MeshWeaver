using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class TypedObjectConverter(IServiceProvider serviceProvider) : System.Text.Json.Serialization.JsonConverter<object>
{
    private readonly ITypeRegistry typeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();

    public override bool CanConvert(Type typeToConvert)
        => true;

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Convert numeric values to strings
            return reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            // Keep string values as-is
            return reader.GetString();
        }
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var rootElement = doc.RootElement;
        return rootElement.TryGetProperty("$type", out var typeName) &&
               typeRegistry.TryGetType(typeName.ToString(), out var type) 
            ? rootElement.Deserialize(type)
            : rootElement;
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        var serialized = JsonSerializer.SerializeToNode(value);
        if (typeRegistry.TryGetTypeName(value.GetType(), out var typeName))
        {
            var obj = (JsonObject)serialized!;
            obj["$type"] = typeName;
        }
        serialized?.WriteTo(writer);
    }


}

