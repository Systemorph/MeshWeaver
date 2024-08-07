using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Options;

public record ColorSchemes
{
    // our default color scheme name. can also be an array of color hash strings.
    [JsonConverter(typeof(SchemeOfColorSchemesJsonConverter))]
    public object Scheme { get; init; } = Palettes.Brewer.PastelOne9;
}

public class SchemeOfColorSchemesJsonConverter : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        if (value is Palettes palette)
        {
            serializer.Serialize(writer, palette.Palette);
        }
        else
        {
            serializer.Serialize(writer, value);
        }
    }

    public override bool CanConvert(Type objectType)
    {
        return true;
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        var token = JToken.ReadFrom(reader);

        if (token.Type == JTokenType.Array)
        {
            return token.ToObject<string[]>();
        }
        return Palettes.FromColor(token.ToString());
    }
}