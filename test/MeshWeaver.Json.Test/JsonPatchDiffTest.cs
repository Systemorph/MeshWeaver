using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.Json.Test;

/// <summary>
/// RFC 6902 diff (<c>CreatePatch</c>). Two independent guarantees:
/// the emitted BYTES match the captured json-everything output (wire compatibility), and the
/// patch is SEMANTICALLY right — <c>apply(diff(a,b)) == b</c> — which is checked by construction
/// over generated documents rather than by example.
/// </summary>
public class JsonPatchDiffTest
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string Diff(string a, string b)
        => JsonSerializer.Serialize(JsonNode.Parse(a).CreatePatch(JsonNode.Parse(b)), Wire);

    // ---- objects --------------------------------------------------------------------

    [Theory]
    [InlineData("""{"a":1}""", """{"a":1}""", "[]")]
    [InlineData("""{"a":1}""", """{"a":2}""", """[{"op":"replace","path":"/a","value":2}]""")]
    [InlineData("""{"a":1}""", """{"a":1,"b":2}""", """[{"op":"add","path":"/b","value":2}]""")]
    [InlineData("""{"a":1,"b":2}""", """{"a":1}""", """[{"op":"remove","path":"/b"}]""")]
    [InlineData("""{"a":{"b":{"c":1}}}""", """{"a":{"b":{"c":2}}}""", """[{"op":"replace","path":"/a/b/c","value":2}]""")]
    [InlineData("""{"a":{"b":1}}""", """{"a":[1]}""", """[{"op":"replace","path":"/a","value":[1]}]""")]
    public void Objects(string a, string b, string expected) => Assert.Equal(expected, Diff(a, b));

    /// <summary>Removals precede additions: the original's members are walked first.</summary>
    [Fact]
    public void OperationOrder_RemovesBeforeAdds()
        => Assert.Equal("""[{"op":"remove","path":"/b"},{"op":"add","path":"/c","value":3}]""",
            Diff("""{"a":1,"b":2}""", """{"a":1,"c":3}"""));

    // ---- null vs missing --------------------------------------------------------------

    /// <summary>
    /// The distinction the wire depends on: an explicit <c>null</c> is a VALUE (replace/add),
    /// an absent member is a structural change (add/remove).
    /// </summary>
    [Theory]
    [InlineData("""{"a":null}""", """{"a":1}""", """[{"op":"replace","path":"/a","value":1}]""")]
    [InlineData("""{"a":1}""", """{"a":null}""", """[{"op":"replace","path":"/a","value":null}]""")]
    [InlineData("""{}""", """{"a":null}""", """[{"op":"add","path":"/a","value":null}]""")]
    [InlineData("""{"a":null}""", """{}""", """[{"op":"remove","path":"/a"}]""")]
    [InlineData("""{"a":null}""", """{"a":null}""", "[]")]
    public void NullVersusMissing(string a, string b, string expected) => Assert.Equal(expected, Diff(a, b));

    // ---- numbers ----------------------------------------------------------------------

    /// <summary>
    /// Numbers compare by VALUE. If they compared by token, every serialize/deserialize
    /// round-trip would emit spurious replaces and the sync stream would never go quiet.
    /// </summary>
    [Theory]
    [InlineData("""{"a":1}""", """{"a":1.0}""")]
    [InlineData("""{"a":0}""", """{"a":-0.0}""")]
    [InlineData("""{"a":1e2}""", """{"a":100}""")]
    [InlineData("""{"a":1.50}""", """{"a":1.5}""")]
    public void EquivalentNumbers_ProduceNoOperations(string a, string b) => Assert.Equal("[]", Diff(a, b));

    [Theory]
    [InlineData("""{"a":1}""", """{"a":"1"}""")]
    [InlineData("""{"a":1}""", """{"a":true}""")]
    [InlineData("""{"a":"true"}""", """{"a":true}""")]
    [InlineData("""{"a":1}""", """{"a":null}""")]
    public void TypeChanges_ProduceAReplace(string a, string b) => Assert.Single(JsonNode.Parse(a).CreatePatch(JsonNode.Parse(b)).Operations);

    /// <summary>A magnitude beyond <see cref="decimal"/> must compare, not crash.</summary>
    [Fact]
    public void HugeNumbers_CompareWithoutThrowing()
    {
        Assert.Equal("[]", Diff("""{"a":1e300}""", """{"a":1e300}"""));
        Assert.Single(JsonNode.Parse("""{"a":1e300}""").CreatePatch(JsonNode.Parse("""{"a":1e301}""")).Operations);
    }

    // ---- arrays -----------------------------------------------------------------------

    [Theory]
    [InlineData("""{"a":[1,2,3]}""", """{"a":[1,2,3]}""", "[]")]
    [InlineData("""{"a":[1,2]}""", """{"a":[1,2,3]}""", """[{"op":"add","path":"/a/2","value":3}]""")]
    [InlineData("""{"a":[1,2,3]}""", """{"a":[1,2]}""", """[{"op":"remove","path":"/a/2"}]""")]
    [InlineData("""{"a":[1,2,3]}""", """{"a":[1,9,3]}""", """[{"op":"replace","path":"/a/1","value":9}]""")]
    [InlineData("""{"a":[{"x":1},{"x":2}]}""", """{"a":[{"x":1},{"x":3}]}""", """[{"op":"replace","path":"/a/1/x","value":3}]""")]
    public void Arrays_PositionalWalk(string a, string b, string expected) => Assert.Equal(expected, Diff(a, b));

    /// <summary>Exactly one side empty ⇒ a whole-array replace, never element operations.</summary>
    [Theory]
    [InlineData("""{"a":[]}""", """{"a":[1,2,3]}""", """[{"op":"replace","path":"/a","value":[1,2,3]}]""")]
    [InlineData("""{"a":[1,2,3]}""", """{"a":[]}""", """[{"op":"replace","path":"/a","value":[]}]""")]
    public void Arrays_EmptySideReplacesWholesale(string a, string b, string expected) => Assert.Equal(expected, Diff(a, b));

    /// <summary>Shrinking walks DESCENDING so each remove index is still valid when applied.</summary>
    [Fact]
    public void Arrays_ShrinkWalksDescending()
        => Assert.Equal(
            """[{"op":"remove","path":"/a/2"},{"op":"replace","path":"/a/1","value":3},{"op":"replace","path":"/a/0","value":2}]""",
            Diff("""{"a":[1,2,3]}""", """{"a":[2,3]}"""));

    /// <summary>A reorder is positional replaces — no <c>move</c> is ever emitted.</summary>
    [Fact]
    public void Arrays_ReorderNeverEmitsMove()
    {
        var patch = JsonNode.Parse("""{"a":[1,2,3]}""").CreatePatch(JsonNode.Parse("""{"a":[3,2,1]}"""));
        Assert.All(patch.Operations, op => Assert.Equal(OperationType.Replace, op.Op));
    }

    [Fact]
    public void Diff_NeverEmitsMoveCopyOrTest()
    {
        foreach (var row in GoldenFixtures.Section("diff"))
        {
            var patch = JsonNode.Parse(row.GetProperty("a").GetString()!)
                .CreatePatch(JsonNode.Parse(row.GetProperty("b").GetString()!));
            Assert.All(patch.Operations, op =>
                Assert.True(op.Op is OperationType.Add or OperationType.Remove or OperationType.Replace,
                    $"{row.GetProperty("name").GetString()} emitted {op.Op}"));
        }
    }

    // ---- RFC 6901 escaping in the emitted PATHS -----------------------------------------

    [Theory]
    [InlineData("""{"a/b":1}""", """{"a/b":2}""", """[{"op":"replace","path":"/a~1b","value":2}]""")]
    [InlineData("""{"a~b":1}""", """{"a~b":2}""", """[{"op":"replace","path":"/a~0b","value":2}]""")]
    [InlineData("""{"a~1b":1}""", """{"a~1b":2}""", """[{"op":"replace","path":"/a~01b","value":2}]""")]
    [InlineData("""{"":1}""", """{"":2}""", """[{"op":"replace","path":"/","value":2}]""")]
    [InlineData("""{"/":1}""", """{"/":2}""", """[{"op":"replace","path":"/~1","value":2}]""")]
    [InlineData("""{}""", """{"a/b":1}""", """[{"op":"add","path":"/a~1b","value":1}]""")]
    [InlineData("""{"a/b":1}""", """{}""", """[{"op":"remove","path":"/a~1b"}]""")]
    public void EscapedKeys(string a, string b, string expected) => Assert.Equal(expected, Diff(a, b));

    // ---- the captured oracle -------------------------------------------------------------

    public static TheoryData<string> DiffCases => GoldenFixtures.Names("diff", "name");

    [Theory]
    [MemberData(nameof(DiffCases))]
    public void MatchesGolden(string name)
    {
        var row = GoldenFixtures.Row("diff", "name", name);
        var a = row.GetProperty("a").GetString()!;
        var b = row.GetProperty("b").GetString()!;
        Assert.Equal(row.GetProperty("patch").GetString(), Diff(a, b));
    }

    // ---- the semantic guarantee, over generated documents ----------------------------------

    /// <summary>
    /// <c>apply(diff(a,b)) == b</c> for 4000 generated (document, mutation) pairs and 4000
    /// independent pairs. Deterministic seed: a failure is reproducible, never a flake.
    /// <para>
    /// This is the property that examples cannot cover — the generator emits keys containing
    /// <c>/</c>, <c>~</c>, <c>~1</c>, empty strings, <c>-</c>, digits, unicode and quotes, nested
    /// in objects and arrays, precisely where a hand-picked case list stops.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(4000, true)]
    [InlineData(4000, false)]
    public void ApplyOfDiff_ReproducesTheTarget(int documents, bool mutate)
    {
        var random = new Random(mutate ? 20260812 : 20260813);
        for (var i = 0; i < documents; i++)
        {
            var a = JsonGenerator.Document(random, 0);
            var b = mutate ? JsonGenerator.Mutate(random, a, 0) : JsonGenerator.Document(random, 0);
            var aText = a.ToJsonString();
            var bText = b.ToJsonString();

            var patch = JsonNode.Parse(aText).CreatePatch(JsonNode.Parse(bText));
            var result = patch.Apply(JsonNode.Parse(aText));

            Assert.True(result.IsSuccess,
                $"seed case #{i}\n  a={aText}\n  b={bText}\n  patch={JsonSerializer.Serialize(patch, Wire)}\n  error={result.Error}");
            Assert.True(result.Result.IsEquivalentTo(JsonNode.Parse(bText)),
                $"seed case #{i}\n  a={aText}\n  b={bText}\n  patch={JsonSerializer.Serialize(patch, Wire)}\n  got={result.Result?.ToJsonString()}");
        }
    }

    /// <summary>A patch round-tripped through its wire bytes applies identically.</summary>
    [Fact]
    public void PatchSurvivesTheWireAndStillApplies()
    {
        var random = new Random(20260814);
        for (var i = 0; i < 1000; i++)
        {
            var a = JsonGenerator.Document(random, 0);
            var b = JsonGenerator.Mutate(random, a, 0);
            var aText = a.ToJsonString();
            var bText = b.ToJsonString();

            var wire = JsonSerializer.Serialize(JsonNode.Parse(aText).CreatePatch(JsonNode.Parse(bText)), Wire);
            var revived = JsonSerializer.Deserialize<JsonPatch>(wire, Wire)!;
            Assert.Equal(wire, JsonSerializer.Serialize(revived, Wire));

            var result = revived.Apply(JsonNode.Parse(aText));
            Assert.True(result.IsSuccess, $"#{i} {result.Error} — {wire}");
            Assert.True(result.Result.IsEquivalentTo(JsonNode.Parse(bText)), $"#{i} — {wire}");
        }
    }

    /// <summary>Identical documents must diff to nothing, whatever their shape.</summary>
    [Fact]
    public void IdenticalDocuments_DiffToTheEmptyPatch()
    {
        var random = new Random(20260815);
        for (var i = 0; i < 1000; i++)
        {
            var document = JsonGenerator.Document(random, 0).ToJsonString();
            Assert.Equal("[]", Diff(document, document));
        }
    }

    /// <summary>Typed objects diff through the serializer overload the sync stream uses.</summary>
    [Fact]
    public void CreatePatch_OverTypedObjects()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var before = new Sample("one", 1, ["x"]);
        var after = new Sample("two", 1, ["x", "y"]);
        Assert.Equal(
            """[{"op":"replace","path":"/name","value":"two"},{"op":"add","path":"/tags/1","value":"y"}]""",
            JsonSerializer.Serialize(before.CreatePatch(after, options), Wire));
    }

    private sealed record Sample(string Name, int Count, string[] Tags);
}
