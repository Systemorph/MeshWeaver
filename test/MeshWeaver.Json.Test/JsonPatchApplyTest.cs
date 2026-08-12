using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.Json.Test;

/// <summary>
/// RFC 6902 apply semantics.
/// <para>
/// 🚨 This is the part json-everything got WRONG: its applier used the ESCAPED pointer segment as
/// the property name, so any key containing <c>/</c> or <c>~</c> — which is every entity-store
/// path in this codebase, since ids are JSON-encoded strings like <c>"acme/Docs/One"</c> —
/// silently created a bogus <c>a~1b</c> member instead of touching <c>a/b</c>. That defect is why
/// <c>JsonSynchronizationStream</c> hand-rolled its own applier. The tests below pin the correct
/// behaviour so the workaround can never be needed again.
/// </para>
/// </summary>
public class JsonPatchApplyTest
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static JsonNode? Apply(string document, string patchJson)
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(patchJson, Wire)!;
        var result = patch.Apply(JsonNode.Parse(document));
        Assert.True(result.IsSuccess, result.Error);
        return result.Result;
    }

    private static string Error(string document, string patchJson)
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(patchJson, Wire)!;
        var result = patch.Apply(JsonNode.Parse(document));
        Assert.False(result.IsSuccess, "expected the patch to fail");
        return result.Error!;
    }

    // ---- the escaping fix ------------------------------------------------------------

    [Theory]
    [InlineData("""{"a/b":1}""", """[{"op":"replace","path":"/a~1b","value":2}]""", """{"a/b":2}""")]
    [InlineData("""{"a~b":1}""", """[{"op":"replace","path":"/a~0b","value":2}]""", """{"a~b":2}""")]
    [InlineData("""{"a~1b":1}""", """[{"op":"replace","path":"/a~01b","value":2}]""", """{"a~1b":2}""")]
    [InlineData("""{"/":1}""", """[{"op":"replace","path":"/~1","value":2}]""", """{"/":2}""")]
    [InlineData("""{"~":1}""", """[{"op":"replace","path":"/~0","value":2}]""", """{"~":2}""")]
    [InlineData("""{}""", """[{"op":"add","path":"/a~1b","value":1}]""", """{"a/b":1}""")]
    [InlineData("""{"a/b":1}""", """[{"op":"remove","path":"/a~1b"}]""", """{}""")]
    [InlineData("""{"a/b":{"c/d":1}}""", """[{"op":"replace","path":"/a~1b/c~1d","value":2}]""", """{"a/b":{"c/d":2}}""")]
    public void EscapedPropertyNames_ResolveToTheRealKey(string document, string patch, string expected)
        => Assert.True(Apply(document, patch).IsEquivalentTo(JsonNode.Parse(expected)));

    /// <summary>The realistic shape: an entity-store path whose id contains slashes.</summary>
    [Fact]
    public void EntityStorePath_WithSlashesInTheId()
    {
        const string store = """{"MeshWeaver.Mesh.Contract.MeshNode":{"\"acme/Docs/One\"":{"version":1}}}""";
        const string patch = """[{"op":"replace","path":"/MeshWeaver.Mesh.Contract.MeshNode/\"acme~1Docs~1One\"/version","value":2}]""";
        var result = Apply(store, patch);
        Assert.Equal(2, result!["MeshWeaver.Mesh.Contract.MeshNode"]!["\"acme/Docs/One\""]!["version"]!.GetValue<int>());
        // and no phantom escaped member was created
        Assert.Null(result["MeshWeaver.Mesh.Contract.MeshNode"]!.AsObject()["\"acme~1Docs~1One\""]);
    }

    // ---- add ---------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"a":1}""", """[{"op":"add","path":"/b","value":2}]""", """{"a":1,"b":2}""")]
    [InlineData("""{"a":1}""", """[{"op":"add","path":"/a","value":9}]""", """{"a":9}""")]
    [InlineData("""{"a":[1,3]}""", """[{"op":"add","path":"/a/1","value":2}]""", """{"a":[1,2,3]}""")]
    [InlineData("""{"a":[1,2]}""", """[{"op":"add","path":"/a/-","value":3}]""", """{"a":[1,2,3]}""")]
    [InlineData("""{"a":[1,2]}""", """[{"op":"add","path":"/a/2","value":3}]""", """{"a":[1,2,3]}""")]
    [InlineData("""{"a":1}""", """[{"op":"add","path":"","value":{"z":1}}]""", """{"z":1}""")]
    [InlineData("""{"a":1}""", """[{"op":"add","path":"/b","value":null}]""", """{"a":1,"b":null}""")]
    public void Add(string document, string patch, string expected)
        => Assert.True(Apply(document, patch).IsEquivalentTo(JsonNode.Parse(expected)));

    [Fact]
    public void Add_BeyondArrayBounds_Fails()
        => Assert.Contains("index", Error("""{"a":[1,2]}""", """[{"op":"add","path":"/a/5","value":3}]"""));

    [Fact]
    public void Add_UnreachableParent_Fails()
        => Assert.Contains("could not be reached", Error("""{"a":1}""", """[{"op":"add","path":"/x/y","value":3}]"""));

    // ---- replace / remove -----------------------------------------------------------------

    [Theory]
    [InlineData("""{"a":1}""", """[{"op":"replace","path":"/a","value":2}]""", """{"a":2}""")]
    [InlineData("""{"a":[1,2]}""", """[{"op":"replace","path":"/a/0","value":9}]""", """{"a":[9,2]}""")]
    [InlineData("""{"a":1}""", """[{"op":"replace","path":"","value":[1]}]""", """[1]""")]
    [InlineData("""{"a":1,"b":2}""", """[{"op":"remove","path":"/b"}]""", """{"a":1}""")]
    [InlineData("""{"a":[1,2,3]}""", """[{"op":"remove","path":"/a/1"}]""", """{"a":[1,3]}""")]
    [InlineData("""{"a":[1,2,3]}""", """[{"op":"remove","path":"/a/-"}]""", """{"a":[1,2]}""")]
    public void ReplaceAndRemove(string document, string patch, string expected)
        => Assert.True(Apply(document, patch).IsEquivalentTo(JsonNode.Parse(expected)));

    [Fact]
    public void Replace_MissingTarget_Fails()
        => Assert.Contains("could not be reached", Error("""{"a":1}""", """[{"op":"replace","path":"/missing","value":2}]"""));

    [Fact]
    public void Remove_MissingTarget_Fails()
        => Assert.Contains("could not be reached", Error("""{"a":1}""", """[{"op":"remove","path":"/missing"}]"""));

    [Fact]
    public void Remove_Root_Fails()
        => Assert.Contains("root", Error("""{"a":1}""", """[{"op":"remove","path":""}]"""));

    [Fact]
    public void Remove_OutOfRangeIndex_Fails()
        => Assert.Contains("could not be reached", Error("""{"a":[1]}""", """[{"op":"remove","path":"/a/5"}]"""));

    // ---- move / copy / test -----------------------------------------------------------------

    [Theory]
    [InlineData("""{"a":1,"b":2}""", """[{"op":"move","from":"/a","path":"/c"}]""", """{"b":2,"c":1}""")]
    [InlineData("""{"a":{"x":1}}""", """[{"op":"move","from":"/a/x","path":"/y"}]""", """{"a":{},"y":1}""")]
    [InlineData("""{"a":[1,2]}""", """[{"op":"move","from":"/a/0","path":"/a/1"}]""", """{"a":[2,1]}""")]
    [InlineData("""{"a/b":1}""", """[{"op":"move","from":"/a~1b","path":"/c"}]""", """{"c":1}""")]
    public void Move(string document, string patch, string expected)
        => Assert.True(Apply(document, patch).IsEquivalentTo(JsonNode.Parse(expected)));

    [Theory]
    [InlineData("""{"a":1}""", """[{"op":"copy","from":"/a","path":"/b"}]""", """{"a":1,"b":1}""")]
    [InlineData("""{"a":{"x":1}}""", """[{"op":"copy","from":"/a","path":"/b"}]""", """{"a":{"x":1},"b":{"x":1}}""")]
    [InlineData("""{"a/b":1}""", """[{"op":"copy","from":"/a~1b","path":"/c"}]""", """{"a/b":1,"c":1}""")]
    public void Copy(string document, string patch, string expected)
        => Assert.True(Apply(document, patch).IsEquivalentTo(JsonNode.Parse(expected)));

    /// <summary>A copied subtree must be detached — mutating the copy must not touch the source.</summary>
    [Fact]
    public void Copy_DeepClonesTheSubtree()
    {
        var result = Apply("""{"a":{"x":1}}""", """[{"op":"copy","from":"/a","path":"/b"}]""")!;
        result["b"]!["x"] = 99;
        Assert.Equal(1, result["a"]!["x"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"a":1}""", """[{"op":"test","path":"/a","value":1}]""")]
    [InlineData("""{"a":1}""", """[{"op":"test","path":"/a","value":1.0}]""")]
    [InlineData("""{"a":null}""", """[{"op":"test","path":"/a","value":null}]""")]
    [InlineData("""{"a":{"x":[1,2]}}""", """[{"op":"test","path":"/a","value":{"x":[1,2]}}]""")]
    [InlineData("""{"a/b":1}""", """[{"op":"test","path":"/a~1b","value":1}]""")]
    public void Test_Passes(string document, string patch) => Apply(document, patch);

    [Theory]
    [InlineData("""{"a":1}""", """[{"op":"test","path":"/a","value":2}]""")]
    [InlineData("""{"a":1}""", """[{"op":"test","path":"/a","value":"1"}]""")]
    [InlineData("""{"a":{"x":1}}""", """[{"op":"test","path":"/a","value":{"x":1,"y":2}}]""")]
    public void Test_FailsOnMismatch(string document, string patch)
        => Assert.Contains("does not match", Error(document, patch));

    [Fact]
    public void Move_MissingSource_Fails()
        => Assert.Contains("could not be reached", Error("""{"a":1}""", """[{"op":"move","from":"/zz","path":"/b"}]"""));

    // ---- document semantics -------------------------------------------------------------

    [Fact]
    public void Apply_LeavesTheSourceUntouched()
    {
        var source = JsonNode.Parse("""{"a":1,"list":[1,2]}""")!;
        var patch = JsonSerializer.Deserialize<JsonPatch>(
            """[{"op":"replace","path":"/a","value":9},{"op":"add","path":"/list/-","value":3}]""", Wire)!;
        patch.Apply(source);
        Assert.Equal("""{"a":1,"list":[1,2]}""", source.ToJsonString());
    }

    [Fact]
    public void Apply_StopsAtTheFirstFailure_AndReportsItsIndex()
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>(
            """[{"op":"add","path":"/a","value":1},{"op":"remove","path":"/nope"},{"op":"add","path":"/b","value":2}]""", Wire)!;
        var result = patch.Apply(JsonNode.Parse("{}"));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.Operation);
    }

    [Fact]
    public void Apply_OperationsRunInOrder()
    {
        var result = Apply("""{"a":[1]}""",
            """[{"op":"add","path":"/a/-","value":2},{"op":"add","path":"/a/-","value":3},{"op":"remove","path":"/a/0"}]""");
        Assert.Equal("""{"a":[2,3]}""", result!.ToJsonString());
    }

    [Fact]
    public void Apply_ValueIsDetachedFromTheOperation()
    {
        var value = JsonNode.Parse("""{"x":1}""")!;
        var patch = new JsonPatch(PatchOperation.Add(JsonPointer.Parse("/a"), value));
        var first = patch.Apply(JsonNode.Parse("{}")).Result!;
        first["a"]!["x"] = 42;
        // Re-applying the SAME patch must not see the mutation.
        var second = patch.Apply(JsonNode.Parse("{}")).Result!;
        Assert.Equal(1, second["a"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void EmptyPatch_IsIdentity()
    {
        var result = new JsonPatch().Apply(JsonNode.Parse("""{"a":1}"""));
        Assert.True(result.IsSuccess);
        Assert.Equal("""{"a":1}""", result.Result!.ToJsonString());
    }

    /// <summary>The typed overload throws (rather than returning a result) — the prior contract.</summary>
    [Fact]
    public void TypedApply_ThrowsOnFailure()
    {
        var patch = new JsonPatch(PatchOperation.Remove(JsonPointer.Parse("/missing")));
        Assert.Throws<InvalidOperationException>(() => patch.Apply(new { a = 1 }));
    }

    [Fact]
    public void TypedApply_RoundTripsThroughTheSerializer()
    {
        var patch = JsonSerializer.Deserialize<JsonPatch>("""[{"op":"replace","path":"/name","value":"two"}]""", Wire)!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var result = patch.Apply(new Sample("one", 1), options);
        Assert.Equal(new Sample("two", 1), result);
    }

    private sealed record Sample(string Name, int Count);
}
