using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A <see cref="MeshNodeError"/> for unreadable content carries the content's SHAPE, never its
/// values. The error travels on the wire, the synthesized <see cref="MeshNodeStreamException"/> is
/// logged by whoever observes the fault, and the record's <c>ToString</c> prints
/// <see cref="MeshNodeError.Diagnostic"/> — so a value placed there reaches Warning+ logs outside
/// row-level security. Both producers in <c>MeshNodeStreamHandle</c> are covered: the read
/// boundary's deserialization error and the refused typed write's diagnostic.
/// </summary>
public class MeshNodeErrorCarriesShapeNotValuesTest
{
    private const string Secret = "abc123-stored-value";
    private static readonly string Json = $$$"""{"$type":"X","secret":"{{{Secret}}}"}""";

    /// <summary>A converter that fails the read the way a contract mismatch does.</summary>
    private sealed class FailingObjectConverter : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new JsonException("missing required properties including: 'content'");

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }

    [Fact]
    public void TheReadBoundarysDeserializationError_NamesShapeNotValues()
    {
        // Negative control: the value IS in the content that fails to read.
        Assert.Contains(Secret, Json);

        var node = new MeshNode("Note", "Space")
        {
            NodeType = "Probe/Note",
            Content = JsonSerializer.Deserialize<JsonElement>(Json),
        };
        var options = new JsonSerializerOptions { Converters = { new FailingObjectConverter() } };

        var thrown = Assert.Throws<MeshNodeStreamException>(
            () => MeshNodeStreamHandle.EnsureTypedContent(node, options));

        Assert.Equal(MeshNodeErrorCode.Deserialization, thrown.Error.Code);
        foreach (var text in new[] { thrown.Error.ToString(), thrown.ToString(), thrown.Error.Diagnostic! })
        {
            Assert.DoesNotContain(Secret, text);
        }
        Assert.Contains("$type=\"X\"", thrown.Error.Diagnostic);
        Assert.Contains("\"secret\"", thrown.Error.Diagnostic);
        Assert.Contains("$type='X'", thrown.Error.Message);
    }

    [Fact]
    public void TheRefusedTypedWritesDiagnostic_NamesShapeNotValues()
    {
        var content = JsonSerializer.Deserialize<JsonElement>(Json);
        Assert.Contains(Secret, content.GetRawText()); // negative control

        var diagnostic = MeshNodeStreamHandle.ContentExcerpt(content);

        Assert.NotNull(diagnostic);
        Assert.DoesNotContain(Secret, diagnostic);
        Assert.Contains("$type=\"X\"", diagnostic);
        Assert.Contains("\"secret\"", diagnostic);
        Assert.Null(MeshNodeStreamHandle.ContentExcerpt(null));
    }
}
