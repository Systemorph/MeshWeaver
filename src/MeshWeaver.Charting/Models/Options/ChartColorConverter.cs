using Newtonsoft.Json;

namespace MeshWeaver.Charting.Models.Options
{
    internal class ChartColorConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => true;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if(value is not null)
                writer.WriteValue(value.ToString());
            else writer.WriteNull();
        }

        public override bool CanRead => false;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) => throw new NotImplementedException();
    }
}
