using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>A warning prefix must not allocate a complete string for a large failed payload.</summary>
public class BoundedJsonDiagnosticTest(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ALargePayload_OnlyAllocatesTheDiagnosticPrefix(int shape)
    {
        var leaf = new string('\n', 256_000);
        var json = JsonSerializer.Serialize(new { text = leaf });
        object content = shape switch
        {
            0 => JsonSerializer.Deserialize<JsonElement>(json),
            1 => JsonNode.Parse(json)!,
            _ => new JsonObject { ["text"] = leaf },
        };
        _ = BoundedJsonDiagnostic.Format(content, 512);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = BoundedJsonDiagnostic.Format(content, 512);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"diagnostic shape {shape}: {allocated:N0} allocated bytes");
        Assert.StartsWith("{\"text\":\"\\n", result);
        Assert.EndsWith("… (truncated)", result);
        Assert.True(result.Length <= 512 + "… (truncated)".Length);
        Assert.True(allocated < 64_000, $"A bounded diagnostic allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASmallPayload_PreservesItsJson(bool asNode)
    {
        const string json = "{\"text\":\"renders empty\",\"values\":[null,true,42]}";
        object content = asNode ? JsonNode.Parse(json)! : JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal(json, BoundedJsonDiagnostic.Format(content, 512));
    }

    [Fact]
    public void ADisposedPayload_DoesNotReplaceTheOriginalWarningWithAnotherFault()
    {
        var document = JsonDocument.Parse("{\"text\":\"test\"}");
        var element = document.RootElement;
        document.Dispose();
        Assert.Equal("<disposed JSON>", BoundedJsonDiagnostic.Format(element, 512));
    }

    [Fact]
    public void AByteCut_DoesNotSplitAUtf8Character()
    {
        var element = JsonSerializer.Deserialize<JsonElement>("{\"text\":\"éééé\"}");
        var result = BoundedJsonDiagnostic.Format(element, 12);
        Assert.Equal("{\"text\":\"é… (truncated)", result);
    }
}
