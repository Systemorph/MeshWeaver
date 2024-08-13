using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.More;

namespace MeshWeaver.Messaging.Serialization;

public class TypedObjectDeserializeConverter(ITypeRegistry typeRegistry, SerializationConfiguration configuration) : JsonConverter<object>
{
    private const string TypeProperty = "$type";

    public override bool CanConvert(Type typeToConvert) => !typeof(IEnumerable).IsAssignableFrom(typeToConvert) && (typeToConvert == typeof(object) || typeToConvert.IsAbstract);

    public override object Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString()
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            _ => DeserializeNode(ref reader, typeToConvert, options)
        };

    private object DeserializeNode(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var node = doc.RootElement.AsNode();
        return Deserialize(node, typeToConvert, options);
    }

    private object Deserialize(JsonNode node, Type typeToConvert, JsonSerializerOptions options)
    {
        if (node is JsonObject jObject)
        {
            if (jObject.TryGetPropertyValue(TypeProperty, out var tn))
            {
                var typeName = tn!.ToString();
                if (!typeRegistry.TryGetType(typeName, out var type) && !configuration.StrictTypeResolution)
                    type = GetTypeByName(typeName);

                if (type == null)
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
