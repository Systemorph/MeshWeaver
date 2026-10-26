using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A content degradation logs the content's SHAPE, never its values.</b> The degradation
/// warnings are Warning-level, so they leave the process for the central log store, which row-level
/// security does not reach. They used to append the stored JSON itself (<c>Raw: {RawJson}</c>), so a
/// node only its owner may read had its content copied to every reader of the logs.
///
/// <para>The production read seams are driven with a payload carrying a distinctive value, and the
/// formatted line, the attached marker's <see cref="Exception.Message"/> and its
/// <see cref="Exception.ToString"/> (what every sink appends) are searched for it. The same records
/// must still carry what diagnoses the row: the path, the NodeType, the <c>$type</c> and the
/// top-level keys.</para>
/// </summary>
public class DegradedContentLogsShapeNotValuesTest
{
    private const string Secret = "abc123-stored-value";
    private const string ProbeNodeType = "Probe/ShapeNotValuesNodeType";
    private const string ProbePath = "Space/AClientNote";

    private static readonly string ProbeJson =
        $$$"""{"$type":"UnregisteredNoteContent","secret":"{{{Secret}}}","nested":{"inner":"{{{Secret}}}"}}""";

    private static MeshNode ProbeNode(object content) =>
        new("AClientNote", "Space") { NodeType = ProbeNodeType, Content = content };

    public static TheoryData<string, bool> SeamsAndShapes() => new()
    {
        { "GetStream", false },
        { "GetQuery", false },
        { "GetQuery", true },
    };

    [Theory]
    [MemberData(nameof(SeamsAndShapes))]
    public void ADegradedReadNeverLogsAStoredValue_ButNamesItsShape(string seam, bool asJsonNode)
    {
        // Negative control: the value IS in what the seam reads, so its absence below is the
        // diagnostic's doing, not the fixture's.
        Assert.Contains(Secret, ProbeJson);

        object content = asJsonNode
            ? JsonNode.Parse(ProbeJson)!
            : JsonSerializer.Deserialize<JsonElement>(ProbeJson);
        var logger = new RecordingLogger();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _ = seam switch
        {
            "GetStream" => MeshNodeStreamCache.ConvertContentJsonElementToTyped(
                ProbeNode(content), options, logger, new MeshContentTypeRegistry()),
            _ => MeshNodeStreamCache.DeserializeContent(
                ProbeNode(content), options, logger, new MeshContentTypeRegistry()),
        };

        var record = Assert.Single(logger.Records);
        var marker = Assert.IsType<MeshNodeContentDegradedException>(record.Exception);

        foreach (var text in new[] { record.Message, marker.Message, marker.ToString() })
        {
            Assert.DoesNotContain(Secret, text);
            Assert.Contains(ProbePath, text);
            Assert.Contains("$type=\"UnregisteredNoteContent\"", text);
            Assert.Contains("\"secret\"", text);
            Assert.Contains("\"nested\"", text);
            Assert.Contains("JSON Object", text);
            Assert.Contains("stayed an untyped JsonElement", text);
        }
        Assert.Contains(ProbeNodeType, record.Message);
        Assert.Contains(ProbeNodeType, marker.Message);
#pragma warning disable CS0618 // the forwarder is asserted EMPTY: the platform no longer carries raw content
        Assert.Null(marker.RawJson);
#pragma warning restore CS0618
    }

    [Fact]
    public void TheShapeNamesTypeKeysKindAndLength_AndNoValue()
    {
        var element = JsonSerializer.Deserialize<JsonElement>("""{"$type":"X","secret":"abc123"}""");

        var shape = ContentShapes.Describe(element);

        Assert.Equal(
            "$type=\"X\", JSON Object, 2 top-level keys [\"$type\", \"secret\"], 31 bytes (values not logged)",
            shape);
        Assert.DoesNotContain("abc123", shape);
    }

    /// <summary>
    /// 🚨 Non-vacuity of the <c>DoesNotContain</c> above: the same search DOES find the token when it
    /// is a KEY (keys are shape and are listed), so a passing absence is not a search that cannot hit.
    /// </summary>
    [Fact]
    public void NegativeControl_ATokenThatIsAKeyIsListed()
    {
        var element = JsonSerializer.Deserialize<JsonElement>("""{"abc123":"value-not-logged"}""");

        var shape = ContentShapes.Describe(element);

        Assert.Contains("abc123", shape);
        Assert.Contains("no $type", shape);
        Assert.DoesNotContain("value-not-logged", shape);
    }

    [Fact]
    public void JsonNodeAndElementAgreeOnTypeAndKeys()
    {
        const string json = """{"$type":"X","secret":"abc123","n":42,"b":true}""";
        var fromElement = ContentShapes.Describe(JsonSerializer.Deserialize<JsonElement>(json));
        var fromNode = ContentShapes.Describe(new JsonObject
        {
            ["$type"] = "X", ["secret"] = "abc123", ["n"] = 42, ["b"] = true,
        });

        Assert.StartsWith("$type=\"X\", JSON Object, 4 top-level keys [\"$type\", \"secret\", \"n\", \"b\"], ", fromElement);
        Assert.StartsWith("$type=\"X\", JSON Object, 4 top-level keys [\"$type\", \"secret\", \"n\", \"b\"], ", fromNode);
        Assert.DoesNotContain("abc123", fromNode);
        Assert.Contains("~", fromNode); // a walked size, said to be approximate
    }

    /// <summary>
    /// The adversarial key is FIRST, so it is inside the displayed prefix: removing the escaping
    /// would put a raw line break in the line, and removing the cut would print all 200 'x'.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADisplayedKeyIsEscapedAndCut(bool asJsonNode)
    {
        var adversarial = "line\nbreak" + new string('x', 200);
        var obj = new JsonObject { [adversarial] = "v" };
        for (var i = 0; i < ContentShapes.MaxKeys + 5; i++)
            obj[$"k{i}"] = i;

        var shape = asJsonNode
            ? ContentShapes.Describe(obj)
            : ContentShapes.Describe(JsonSerializer.SerializeToElement(obj));

        // escaped (\n as two characters) and cut to MaxNameChars characters of the escaped form
        var expected = "\"line\\nbreak" + new string('x', ContentShapes.MaxNameChars - "line\\nbreak".Length) + "…\"";
        if (asJsonNode) // cut BEFORE escaping, so one more 'x' fits
            expected = "\"line\\nbreak" + new string('x', ContentShapes.MaxNameChars - "line\nbreak".Length) + "…\"";
        Assert.Contains("[" + expected + ", ", shape);
        Assert.DoesNotContain("\n", shape);
        Assert.DoesNotContain(new string('x', ContentShapes.MaxNameChars), shape);
        Assert.Contains($"{ContentShapes.MaxKeys + 6} top-level keys", shape);
        Assert.Contains("… 6 more", shape);

        // Negative control: the key really carries a line break and more than the cut.
        Assert.Contains("\n", adversarial);
        Assert.True(adversarial.Length > ContentShapes.MaxNameChars);
    }

    /// <summary>
    /// An oversized KEY or <c>$type</c> must not be decoded whole just to print 64 characters of it.
    /// </summary>
    [Theory]
    [InlineData("key")]
    [InlineData("type")]
    public void AnOversizedKeyOrType_IsNotDecodedWhole(string where)
    {
        string Build(char c)
        {
            var big = new string(c, 512_000);
            return where == "key"
                ? JsonSerializer.Serialize(new Dictionary<string, int> { [big] = 1 })
                : JsonSerializer.Serialize(new Dictionary<string, string> { ["$type"] = big });
        }
        _ = ContentShapes.Describe(JsonSerializer.Deserialize<JsonElement>(Build('w'))); // warm-up
        var element = JsonSerializer.Deserialize<JsonElement>(Build('z'));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var shape = ContentShapes.Describe(element);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains(new string('z', ContentShapes.MaxNameChars) + "…", shape);
        Assert.True(allocated < 64_000, $"Describing an oversized {where} allocated {allocated:N0} bytes.");
    }

    /// <summary>
    /// A FRESH parsed tree (its containers still lazy) with a large numeric array: sizing must not
    /// materialise the array or format each number on the heap. The size is then a lower bound.
    /// </summary>
    [Fact]
    public void AFreshParsedTreeWithALargeNumericArray_IsNotMaterialised()
    {
        var json = "{\"$type\":\"X\",\"values\":[" + string.Join(",", Enumerable.Range(0, 100_000)) + "]}";
        _ = ContentShapes.Describe(JsonNode.Parse(json)!); // warm-up on ANOTHER tree
        var fresh = JsonNode.Parse(json)!;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var shape = ContentShapes.Describe(fresh);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("[\"$type\", \"values\"]", shape);
        Assert.Contains("≥", shape);
        Assert.True(allocated < 64_000, $"Describing a fresh 100k-number tree allocated {allocated:N0} bytes.");
    }

    /// <summary>
    /// Constructed numeric leaves are sized without a heap string each, and past the entry budget
    /// the size is reported as a lower bound.
    /// </summary>
    [Fact]
    public void ALargeConstructedNumericArray_IsBudgeted()
    {
        var array = new JsonArray();
        for (var i = 0; i < 100_000; i++)
            array.Add(i * 1.5);
        _ = ContentShapes.Describe(array);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var shape = ContentShapes.Describe(array);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.StartsWith("no $type, JSON Array, ≥", shape);
        Assert.True(allocated < 64_000, $"Describing a 100k-number array allocated {allocated:N0} bytes.");

        // Negative control: a small array is sized completely, so '≥' above is the budget's doing.
        Assert.StartsWith("no $type, JSON Array, ~", ContentShapes.Describe(new JsonArray(1, 2, 3)));
    }

    [Fact]
    public void NonObjectsAndNonJson_NameTheirKindOnly()
    {
        Assert.Equal("no $type, JSON Array, 7 bytes (values not logged)",
            ContentShapes.Describe(JsonSerializer.Deserialize<JsonElement>("""["abc"]""")));
        Assert.Equal("no content (null)", ContentShapes.Describe(null));
        Assert.DoesNotContain("abc123", ContentShapes.Describe("abc123"));
        Assert.DoesNotContain("abc123", ContentShapes.Describe(new { secret = "abc123" }));
    }

    [Fact]
    public void ADisposedDocumentIsNamed_NotRethrown()
    {
        var document = JsonDocument.Parse("""{"secret":"abc123"}""");
        var element = document.RootElement;
        document.Dispose();
        Assert.Equal("<disposed JSON>", ContentShapes.Describe(element));
    }

    /// <summary>
    /// Describing a large payload must not materialise it — the diagnostic replaced one that was
    /// bounded for exactly that reason.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ALargePayload_IsDescribedWithoutAllocatingIt(int shape)
    {
        var leaf = new string('\n', 256_000);
        var json = JsonSerializer.Serialize(new { text = leaf });
        object content = shape switch
        {
            0 => JsonSerializer.Deserialize<JsonElement>(json),
            1 => JsonNode.Parse(json)!,
            _ => new JsonObject { ["text"] = leaf },
        };
        _ = ContentShapes.Describe(content);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ContentShapes.Describe(content);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("[\"text\"]", result);
        Assert.True(allocated < 64_000, $"Describing a large payload allocated {allocated:N0} bytes.");
    }

    private sealed record Record(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Record> records = [];

        public IReadOnlyList<Record> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                records.Add(new Record(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
