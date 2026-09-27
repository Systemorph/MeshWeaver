using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>Query re-typing must not allocate a second UTF-16 copy of the whole JSON payload.</summary>
public class QueryContentAllocationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int LeafChars = 256_000;
    private record QueryAllocationContent(string Text);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQueryRead_MaterializesTheLeafWithoutAWholeJsonString(bool asJsonNode)
    {
        var host = GetHost();
        host.ServiceProvider.GetRequiredService<ITypeRegistry>()
            .WithType(typeof(QueryAllocationContent), nameof(QueryAllocationContent));
        var options = host.JsonSerializerOptions;
        var json = $"{{\"$type\":\"{nameof(QueryAllocationContent)}\",\"text\":\"{new string('a', LeafChars)}\"}}";
        var node = new MeshNode("allocation", "TestData")
        {
            Content = asJsonNode ? JsonNode.Parse(json) : JsonSerializer.Deserialize<JsonElement>(json),
        };

        MeshNode Read() => MeshNodeStreamCache.DeserializeContent(node, options, NullLogger.Instance, null);
        Assert.Equal(LeafChars, Assert.IsType<QueryAllocationContent>(Read().Content).Text.Length);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = Read();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(LeafChars, Assert.IsType<QueryAllocationContent>(result.Content).Text.Length);
        Output.WriteLine($"query content ({(asJsonNode ? "JsonNode" : "JsonElement")}): {allocated:N0} allocated bytes");
        Assert.True(allocated < 1.9 * 2 * LeafChars,
            $"A leaf needs {2 * LeafChars:N0} UTF-16 bytes. A complete raw JSON string adds another "
            + $"copy before the leaf is materialized; this read allocated {allocated:N0} bytes.");
    }
}
