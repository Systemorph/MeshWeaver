using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.More;
using OpenSmc.Serialization;
using OpenSmc.Utils;

namespace OpenSmc.Messaging.Serialization;

public class TypedObjectConverter(ITypeRegistry typeRegistry, Type excluded) : System.Text.Json.Serialization.JsonConverter<object>
{
    private const string TypeProperty = "$type";
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert != excluded;

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            // Convert numeric values to strings
            return reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString(CultureInfo.InvariantCulture);

        if (reader.TokenType == JsonTokenType.String)
            // Keep string values as-is
            return reader.GetString();

        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        var node = doc.RootElement.AsNode();
        return Deserialize(node, typeToConvert, options);
    }

    private object Deserialize(JsonNode node, Type typeToConvert, JsonSerializerOptions options)
    {

        var clonedOptions = CloneOptions(options);

        if (node is JsonObject jObject)
        {
            if (jObject.TryGetPropertyValue(TypeProperty, out var typeName))
            {
                var type = GetTypeByName(typeName!.ToString());

                if (type == null && !typeRegistry.TryGetType(typeName!.ToString(), out type))
                    return node;


                clonedOptions.Converters.Add(new TypedObjectConverter(typeRegistry, type));

                return node.Deserialize(type, clonedOptions);

                
            }

            clonedOptions.Converters.Add(new TypedObjectConverter(typeRegistry, typeToConvert));
            return node.Deserialize(typeToConvert, clonedOptions);
        }

        if (node is JsonArray jArray)
        {
            return jArray.Select(e => e.Deserialize(typeof(object), options));
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
        var clonedOptions = CloneOptions(options);
        var serialized = JsonSerializer.SerializeToNode(value, clonedOptions);
        serialized = Traverse(serialized, value, null);
        serialized.WriteTo(writer);
    }

    private JsonSerializerOptions CloneOptions(JsonSerializerOptions options)
    {
        var clonedOptions = new JsonSerializerOptions(options);
        clonedOptions.Converters.Remove(this);
        return clonedOptions;
    }

    private JsonNode Traverse(JsonNode serialized, object value, Type type)
    {
        if (value == null || serialized is not JsonObject obj)
            return serialized;

        var valueType = value.GetType();
        if (valueType != type)
            obj[TypeProperty] = typeRegistry.GetOrAddTypeName(valueType) ;

        type = valueType;

        foreach (var prop in type.GetProperties())
        {
            var jsonName = prop.Name.ToCamelCase();
            if(obj.TryGetPropertyValue(jsonName, out var node))
                obj[jsonName] = Traverse(node, prop.GetValue(value), prop.PropertyType);
        }

        return obj;
    }

}

