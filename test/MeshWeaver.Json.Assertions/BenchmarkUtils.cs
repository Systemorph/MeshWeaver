using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Json.Patch;

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
            var parsedModel = JsonSerializer.Deserialize<JsonElement>(modelSerialized);
            var parsedBenchmark = JsonSerializer.Deserialize<JsonElement>(benchmark);
            var patch = parsedModel.CreatePatch(parsedBenchmark);

            patch.Operations.Should().BeNullOrEmpty("JSON should match the benchmark. Differences:\n" 
                                                    + string.Join('\n', patch.Operations.Select(Format))
                                                    + "\n\n");
        }
    }

    private static string Format(PatchOperation op)
    {
        return op.Op switch
        {
            OperationType.Add => $"{op.Path} missing {op.Value}",
            OperationType.Remove => $"{op.Path} should be removed",
            OperationType.Replace => $"{op.Path} should be {op.Value}",
            _ => $"{op.Path} {op.Op} {op.Value}"
        };
    }

    private static JsonSerializerOptions CloneOptions(JsonSerializerOptions options) => new(options);
}
