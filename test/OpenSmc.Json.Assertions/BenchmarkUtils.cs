//#define REGENERATE

using FluentAssertions;
using OpenSmc.Serialization;
using Newtonsoft.Json.Linq;

namespace OpenSmc.Json.Assertions;

public static class BenchmarkUtils
{
    public static void WriteBenchmark(string fileName, string serialized)
    {
        File.WriteAllText(fileName, serialized);
    }

    public static void JsonShouldMatch(this object model, ISerializationService serializationService, string fileName)
    {
        var modelSerialized = JObject.FromObject(model, ((SerializationService)serializationService).Serializer).ToString();
        var filePath = Path.Combine(@"..\..\..\Json", fileName);
#if REGENERATE
            var benchmark = JObject.FromObject(model, ((SerializationService)serializationService).Serializer).ToString();
            BenchmarkUtils.WriteBenchmark(filePath, benchmark);
#else
        var benchmark = File.ReadAllText(filePath);
#endif
        modelSerialized.Should().Be(benchmark);
    }
}