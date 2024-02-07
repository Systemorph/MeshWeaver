using Newtonsoft.Json;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Serialization;

public class FactoryConverter(TypeFactory typeFactory) : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        throw new NotSupportedException("CustomCreationConverter should only be used while deserializing.");
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        object value = Create(objectType);
        if (value == null)
        {
            throw new JsonSerializationException("No object created.");
        }

        serializer.Populate(reader, value);
        return value;
    }

    public object Create(Type objectType)
    {
        return typeFactory.Factory(objectType);
    }

    public override bool CanConvert(Type objectType)
    {
        return typeFactory.Filter(objectType);
    }

    public override bool CanWrite => false;
}


