using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.Json.Test;

/// <summary>
/// 🚨 THE wire-compatibility gate. Every expectation is json-everything's literal output,
/// captured before the package was removed (#1231).
/// <para>
/// A patch leaves this process as raw RFC 6902 JSON inside <c>DataChangedEvent.Change</c>, and the
/// React, gRPC-web and Python clients parse those bytes. Those clients are NOT built by this
/// repo's CI, so nothing else in the pipeline would catch a shape change: not a compile, not an
/// integration test, not a deploy. This suite is the only thing standing between a refactor and a
/// silent client break — if it fails, the answer is to change the code back, never the fixture.
/// </para>
/// </summary>
public class JsonPatchWireFormatTest
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    // ---- the literal bytes of every operation shape ----------------------------------

    [Theory]
    [InlineData("add-scalar")]
    [InlineData("add-null")]
    [InlineData("add-root")]
    [InlineData("add-escaped")]
    [InlineData("add-object")]
    [InlineData("replace-scalar")]
    [InlineData("replace-null")]
    [InlineData("remove")]
    [InlineData("remove-root")]
    [InlineData("test")]
    [InlineData("move")]
    [InlineData("copy")]
    [InlineData("multi")]
    [InlineData("empty")]
    public void OperationSerialization_IsByteIdenticalToTheCapturedShape(string name)
    {
        var expected = GoldenFixtures.Row("operationSerialization", "name", name).GetProperty("json").GetString();
        Assert.Equal(expected, JsonSerializer.Serialize(Build(name), Wire));
    }

    private static JsonPatch Build(string name) => name switch
    {
        "add-scalar" => new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), JsonValue.Create(1))),
        "add-null" => new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), null)),
        "add-root" => new JsonPatch(PatchOperation.Add(JsonPointer.Empty, JsonNode.Parse("""{"x":1}"""))),
        "add-escaped" => new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a~1b/c~0d"), JsonValue.Create("v"))),
        "add-object" => new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), JsonNode.Parse("""{"x":[1,null,true],"ü":"<b>"}"""))),
        "replace-scalar" => new JsonPatch(PatchOperation.Replace(JsonPointer.Parse("/a"), JsonValue.Create("s"))),
        "replace-null" => new JsonPatch(PatchOperation.Replace(JsonPointer.Parse("/a"), null)),
        "remove" => new JsonPatch(PatchOperation.Remove(JsonPointer.Parse("/a/0"))),
        "remove-root" => new JsonPatch(PatchOperation.Remove(JsonPointer.Empty)),
        "test" => new JsonPatch(PatchOperation.Test(JsonPointer.Parse("/a"), JsonValue.Create(true))),
        "move" => new JsonPatch(PatchOperation.Move(JsonPointer.Parse("/from"), JsonPointer.Parse("/to"))),
        "copy" => new JsonPatch(PatchOperation.Copy(JsonPointer.Parse("/from"), JsonPointer.Parse("/to"))),
        "multi" => new JsonPatch(
            PatchOperation.Add(JsonPointer.Parse("/a"), JsonValue.Create(1)),
            PatchOperation.Remove(JsonPointer.Parse("/b")),
            PatchOperation.Replace(JsonPointer.Parse("/c"), JsonValue.Create("z"))),
        "empty" => new JsonPatch(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown fixture")
    };

    /// <summary>
    /// The property ORDER is contract: <c>op</c>, <c>path</c>, then <c>value</c> or <c>from</c>.
    /// A reordering deserializes fine everywhere and still breaks a byte-comparing client.
    /// </summary>
    [Fact]
    public void PropertyOrder_IsOpThenPathThenValueOrFrom()
    {
        Assert.Equal("""[{"op":"add","path":"/a","value":1}]""",
            JsonSerializer.Serialize(new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), JsonValue.Create(1)))));
        Assert.Equal("""[{"op":"move","path":"/to","from":"/from"}]""",
            JsonSerializer.Serialize(new JsonPatch(PatchOperation.Move(JsonPointer.Parse("/from"), JsonPointer.Parse("/to")))));
        Assert.Equal("""[{"op":"remove","path":"/a"}]""",
            JsonSerializer.Serialize(new JsonPatch(PatchOperation.Remove(JsonPointer.Parse("/a")))));
    }

    /// <summary><c>remove</c> emits no <c>value</c>; <c>add</c> emits it even when null.</summary>
    [Fact]
    public void ValueMember_PresentForAddReplaceTest_AbsentForRemove()
    {
        Assert.DoesNotContain("value", JsonSerializer.Serialize(new JsonPatch(PatchOperation.Remove(JsonPointer.Parse("/a")))));
        Assert.Contains("\"value\":null", JsonSerializer.Serialize(new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), null))));
        Assert.Contains("\"value\":null", JsonSerializer.Serialize(new JsonPatch(PatchOperation.Replace(JsonPointer.Parse("/a"), null))));
        Assert.DoesNotContain("\"from\"", JsonSerializer.Serialize(new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), JsonValue.Create(1)))));
    }

    /// <summary>
    /// The verbs and member names must survive a camelCase / naming-policy change on the hub's
    /// options — they are literals, not policy-derived.
    /// </summary>
    [Fact]
    public void WireShape_IsNamingPolicyInvariant()
    {
        var patch = new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/SomeName"), JsonValue.Create(1)));
        const string expected = """[{"op":"add","path":"/SomeName","value":1}]""";
        Assert.Equal(expected, JsonSerializer.Serialize(patch));
        Assert.Equal(expected, JsonSerializer.Serialize(patch, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(expected, JsonSerializer.Serialize(patch, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper
        }));
    }

    // ---- deserialization -------------------------------------------------------------

    [Theory]
    [InlineData("""[{"op":"add","path":"/a","value":1}]""")]
    [InlineData("""[{"value":1,"path":"/a","op":"add"}]""")]
    [InlineData("""[{"op":"replace","path":"/a~1b","value":null}]""")]
    [InlineData("""[{"op":"remove","path":"/a/0"}]""")]
    [InlineData("""[{"op":"move","from":"/a","path":"/b"}]""")]
    [InlineData("""[{"op":"copy","path":"/b","from":"/a"}]""")]
    [InlineData("""[{"op":"test","path":"","value":{"k":[1,2]}}]""")]
    [InlineData("""[]""")]
    [InlineData("""[{"op":"add","path":"/a","value":1,"unknown":"ignored"}]""")]
    public void Deserialize_AcceptsAnyMemberOrderAndIgnoresUnknowns(string json)
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(json, Wire);
        Assert.NotNull(patch);
        // Re-serializing must produce the canonical order regardless of the input order.
        var again = JsonSerializer.Deserialize<JsonPatch>(JsonSerializer.Serialize(patch, Wire), Wire);
        Assert.Equal(patch, again);
    }

    [Theory]
    [InlineData("""[{"op":"add","path":"/a"}]""")]
    [InlineData("""[{"op":"replace","path":"/a"}]""")]
    [InlineData("""[{"op":"test","path":"/a"}]""")]
    [InlineData("""[{"op":"move","path":"/a"}]""")]
    [InlineData("""[{"op":"copy","path":"/a"}]""")]
    [InlineData("""[{"op":"add","value":1}]""")]
    [InlineData("""[{"op":"bogus","path":"/a","value":1}]""")]
    public void Deserialize_RejectsIncompleteOperations(string json)
        => Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<JsonPatch>(json, Wire));

    /// <summary>
    /// A malformed <c>path</c> surfaces as a POINTER-parse failure, not a
    /// <see cref="JsonException"/> — matching the previous implementation, so a
    /// <c>catch (JsonException)</c> around patch deserialization keeps not swallowing it.
    /// </summary>
    [Fact]
    public void Deserialize_MalformedPointerThrowsPointerParseException()
        => Assert.Throws<PointerParseException>(
            () => JsonSerializer.Deserialize<JsonPatch>("""[{"op":"add","path":"bad","value":1}]""", Wire));

    [Fact]
    public void Roundtrip_PreservesTheExactBytes()
    {
        foreach (var row in GoldenFixtures.Section("diff"))
        {
            var expected = row.GetProperty("patch").GetString()!;
            var patch = JsonSerializer.Deserialize<JsonPatch>(expected, Wire)!;
            Assert.Equal(expected, JsonSerializer.Serialize(patch, Wire));
        }
    }

    /// <summary>
    /// The <c>$type</c> discriminator is the SHORT type name, so the class must stay
    /// <c>JsonPatch</c>: it is registered in the hub TypeRegistry and rides stream payloads.
    /// </summary>
    [Fact]
    public void TypeName_StaysJsonPatch()
    {
        Assert.Equal("JsonPatch", typeof(JsonPatch).Name);
        Assert.Equal("PatchOperation", typeof(PatchOperation).Name);
        Assert.Equal("JsonPointer", typeof(JsonPointer).Name);
    }
}
