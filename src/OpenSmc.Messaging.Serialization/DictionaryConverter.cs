using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class DictionaryConverter : JsonConverter<IDictionary>
{

    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(IDictionary).IsAssignableFrom(typeToConvert);
    }

    public override IDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dictionaryType = typeof(Dictionary<,>);
        var keyType = typeof(object);
        var valueType = typeof(object);

        if (typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == dictionaryType)
        {
            var genericArgs = typeToConvert.GetGenericArguments();
            if (genericArgs.Length == 2)
            {
                keyType = genericArgs[0];
                valueType = genericArgs[1];
            }
        }

        var helperType = typeof(DictionaryHelper<,>).MakeGenericType(keyType, valueType);
        var helper = Activator.CreateInstance(helperType);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = JsonSerializer.Deserialize(ref reader, keyType, options);
                reader.Read();

                var value = JsonSerializer.Deserialize(ref reader, valueType, options);
                helperType.GetMethod("Add").Invoke(helper, new[] { key, value });
            }
        }

        return (IDictionary)helperType.GetProperty("Dictionary")!.GetValue(helper);
    }

    public override void Write(Utf8JsonWriter writer, IDictionary value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (DictionaryEntry kvp in value)
        {
            if (kvp.Value == null)
                continue;
            writer.WritePropertyName(JsonSerializer.Serialize(kvp.Key, kvp.Key.GetType(), options));
            JsonSerializer.Serialize(writer, kvp.Value, kvp.Value.GetType(), options);
        }

        writer.WriteEndObject();
    }
}

// Helper class to wrap ImmutableDictionary<object, object>
public class DictionaryHelper<TKey, TValue>
{
    private readonly ImmutableDictionary<TKey, TValue>.Builder builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();

    public void Add(TKey key, TValue value)
    {
        builder.Add(key, value);
    }

    public ImmutableDictionary<TKey, TValue> Dictionary => builder.ToImmutable();
}

