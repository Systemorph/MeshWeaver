using System.Text.Json;
using FluentAssertions;

namespace MeshWeaver.Json.Assertions;

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
        var clonedOptions = CloneOptions(options);
        clonedOptions.WriteIndented = true;
        var modelSerialized = JsonSerializer.Serialize(model, model.GetType(), clonedOptions);
        var filePath = Path.Combine(@"../../../Json", fileName);
        if (!File.Exists(filePath))
        {
            var benchmark = JsonSerializer.Serialize(model, model.GetType(), clonedOptions);
            await BenchmarkUtils.WriteBenchmarkAsync(filePath, benchmark);
        }
        else
        {
            var benchmark = await File.ReadAllTextAsync(filePath);
            modelSerialized.Should().Be(benchmark);
        }
    }

    private static JsonSerializerOptions CloneOptions(JsonSerializerOptions options) => new(options);
}
