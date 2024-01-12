using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenSmc.Serialization;

public class ObjectDeserializationConverter : JsonConverter
{
    private readonly IEventsRegistry eventsRegistry;

    public ObjectDeserializationConverter(IEventsRegistry eventsRegistry)
    {
        this.eventsRegistry = eventsRegistry;
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer1)
    {
        throw new NotSupportedException();
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var token = JToken.ReadFrom(reader);
        if (token is JObject jObject)
        {
            var typeName = jObject["$type"]?.Value<string>();
            if (!string.IsNullOrEmpty(typeName))
            {
                if (!eventsRegistry.TryGetType(typeName, out var type) && (type = GetTypeByName(typeName)) == null)
                {
                    // potentially convert to RawJson
                    return token;
                }

                objectType = type;
            }

            return token.ToObject(objectType, serializer);
        }

        if (token is JArray jArray)
        {
            return jArray.ToObject(typeof(IEnumerable<object>), serializer);
        }

        return token.ToObject(objectType);

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

    public override bool CanWrite => false;

    public override bool CanConvert(Type objectType) => objectType == typeof(object);
}