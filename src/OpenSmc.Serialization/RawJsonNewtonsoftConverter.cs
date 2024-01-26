using System.Globalization;
using Newtonsoft.Json;

namespace OpenSmc.Serialization;

public class RawJsonNewtonsoftConverter : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        var rawJson = (RawJson)value!;

        if (string.IsNullOrEmpty(rawJson.Content))
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRawValue(rawJson.Content);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        using StringWriter sw = new StringWriter(CultureInfo.InvariantCulture);
        using JsonTextWriter jsonWriter = new JsonTextWriter(sw);
        jsonWriter.WriteToken(reader);
        return new RawJson(sw.ToString());
    }

    public override bool CanConvert(Type objectType) => objectType == typeof(RawJson);
}