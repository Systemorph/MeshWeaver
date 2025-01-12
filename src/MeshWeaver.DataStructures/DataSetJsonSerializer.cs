using Newtonsoft.Json;

namespace MeshWeaver.DataStructures;

public class DataSetJsonSerializer : IDataSetSerializer
{
    public static readonly IDataSetSerializer Instance = new DataSetJsonSerializer();

    private DataSetJsonSerializer()
    {
    }

    private static readonly JsonSerializerSettings IndentedSerializerSettings = GetJsonSettings(Formatting.Indented);
    private static readonly JsonSerializerSettings NonIndentedSerializerSettings = GetJsonSettings(Formatting.None);

    private static JsonSerializerSettings GetJsonSettings(Formatting formatting)
    {
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            Formatting = formatting
        };
        settings.Converters.Add(new DataTableConverter());
        settings.Converters.Add(new DataSetConverter());

        return settings;
    }

    public string Serialize(IDataSet dataSet, bool indent)
    {
        var settings = indent ? IndentedSerializerSettings : NonIndentedSerializerSettings;
        return JsonConvert.SerializeObject(dataSet, settings);
    }

    public IDataSet Parse(TextReader reader)
    {
        var serializer = JsonSerializer.CreateDefault(NonIndentedSerializerSettings);
        return (IDataSet)serializer.Deserialize(reader, typeof(DataSet));
    }
}
