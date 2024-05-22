//#define REGENERATE

using System.Text.Json;
using FluentAssertions;

namespace OpenSmc.Json.Assertions;

public static class BenchmarkUtils
{
    public static Task WriteBenchmarkAsync(string fileName, string serialized)
    {
        return File.WriteAllTextAsync(fileName, serialized);
    }

    public static async Task JsonShouldMatch(
        this object model,
        JsonSerializerOptions options,
        string fileName
    )
    {
        options.WriteIndented = true;
        fileName = $"{fileName}.json";
        var modelSerialized = JsonSerializer.Serialize(model, model.GetType(), options);
        var filePath = Path.Combine(@"..\..\..\Json", fileName);
#if REGENERATE
        var benchmark = JsonSerializer.Serialize(model, model.GetType(), options);
        await BenchmarkUtils.WriteBenchmarkAsync(filePath, benchmark);
#else
        var benchmark = await File.ReadAllTextAsync(filePath);
#endif
        modelSerialized.Should().Be(benchmark);
    }
}
