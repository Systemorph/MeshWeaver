using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;

namespace OpenSmc.Messaging.Serialization;

public class TypedObjectDeserializeConverter(ITypeRegistry typeRegistry) : JsonConverter<object>
{
    private const string TypeProperty = "$type";

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(object) || typeToConvert.IsAbstract || typeToConvert.IsInterface;

    public override object Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Number)
            // Convert numeric values to strings
            return reader.TryGetInt64(out long l)
                ? l.ToString()
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture);

        if (reader.TokenType == JsonTokenType.String)
            // Keep string values as-is
            return reader.GetString();

        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var node = doc.RootElement.AsNode();
        return Deserialize(node, typeToConvert, options);
    }

    private object Deserialize(JsonNode node, Type typeToConvert, JsonSerializerOptions options)
    {
        if (node is JsonObject jObject)
        {
            if (jObject.TryGetPropertyValue(TypeProperty, out var typeName))
            {
                var type = GetTypeByName(typeName!.ToString());

                if (type == null && !typeRegistry.TryGetType(typeName!.ToString(), out type))
                    return node.DeepClone();

                return node.Deserialize(type, options);
            }

            if (typeToConvert == typeof(object))
                return node.DeepClone();

            return node.Deserialize(typeToConvert, options);
        }

        if (node is JsonArray jArray)
        {
            return jArray.Select(e => e.Deserialize(typeof(object), options)).ToArray();
        }

        return node.Deserialize(typeToConvert, options);

        Type GetTypeByName(string typeName)
        {
            try
            {
                return Type.GetType(typeName);
            }
            catch (Exception)
            {
                return null;
                //ignore
            }
        }
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }
}

public class TypedObjectSerializeConverter(ITypeRegistry typeRegistry, Type exclude)
    : JsonConverter<object>
{
    private const string TypeProperty = "$type";

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert != exclude
        && !typeof(IEnumerable).IsAssignableFrom(typeToConvert)
        && !typeof(JsonNode).IsAssignableFrom(typeToConvert);

    public override object Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        var clonedOptions = CloneOptions(options);
        clonedOptions.Converters.Add(
            new TypedObjectSerializeConverter(typeRegistry, value.GetType())
        );
        var serialized = JsonSerializer.SerializeToNode(value, value.GetType(), clonedOptions);
        if (serialized is JsonObject obj && value is not IDictionary && !obj.ContainsKey(TypeProperty))
            obj[TypeProperty] = typeRegistry.GetOrAddTypeName(value.GetType());
        ;

        serialized!.WriteTo(writer);
    }

    private JsonSerializerOptions CloneOptions(JsonSerializerOptions options)
    {
        var clonedOptions = new JsonSerializerOptions(options);
        clonedOptions.Converters.Remove(this);
        return clonedOptions;
    }
}
