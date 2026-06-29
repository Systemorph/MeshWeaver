#pragma warning disable CS1591

using System.Text.Json.Nodes;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure, mesh-free coverage of <see cref="MeshOperations.MergePatch"/> — the RFC 7396
/// JSON Merge Patch used by <c>Patch</c> so a content delta updates only the keys it
/// carries and preserves the rest (the fix for the 2026-06-13 content-clobber, where
/// {"content":{"logo":…}} wiped name/description/body off a live Space).
/// </summary>
public class MeshOperationsMergePatchTest
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void MergePatch_UpdatesProvidedKey_PreservesOmittedSiblings()
    {
        var merged = MeshOperations.MergePatch(
            Parse("""{"name":"Widget","price":1.0,"quantity":1}"""),
            Parse("""{"price":2.5}"""))!.AsObject();

        merged["price"]!.GetValue<double>().Should().Be(2.5);     // updated
        merged["name"]!.GetValue<string>().Should().Be("Widget"); // preserved
        merged["quantity"]!.GetValue<int>().Should().Be(1);       // preserved
    }

    [Fact]
    public void MergePatch_NullMember_DeletesThatKeyOnly()
    {
        var merged = MeshOperations.MergePatch(
            Parse("""{"a":1,"b":2}"""),
            Parse("""{"b":null}"""))!.AsObject();

        Assert.False(merged.ContainsKey("b")); // a null member deletes its key (RFC 7396)
        merged["a"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void MergePatch_NestedObjects_MergeRecursively()
    {
        var merged = MeshOperations.MergePatch(
            Parse("""{"meta":{"x":1,"y":2}}"""),
            Parse("""{"meta":{"y":9,"z":3}}"""))!.AsObject();

        var meta = merged["meta"]!.AsObject();
        meta["x"]!.GetValue<int>().Should().Be(1);   // preserved deep
        meta["y"]!.GetValue<int>().Should().Be(9);   // updated deep
        meta["z"]!.GetValue<int>().Should().Be(3);   // added deep
    }

    [Fact]
    public void MergePatch_Arrays_ReplaceWholesale()
    {
        var merged = MeshOperations.MergePatch(
            Parse("""{"tags":["a","b","c"]}"""),
            Parse("""{"tags":["x"]}"""))!.AsObject();

        var tags = merged["tags"]!.AsArray();
        tags.Count.Should().Be(1);  // arrays are replaced, not element-merged (RFC 7396)
        tags[0]!.GetValue<string>().Should().Be("x");
    }

    [Fact]
    public void MergePatch_AddsKeyMissingFromTarget()
    {
        var merged = MeshOperations.MergePatch(
            Parse("""{"a":1}"""),
            Parse("""{"b":2}"""))!.AsObject();

        merged["a"]!.GetValue<int>().Should().Be(1);
        merged["b"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void MergePatch_DoesNotMutateEitherArgument()
    {
        var target = Parse("""{"a":1}""");
        var patch = Parse("""{"a":2,"b":3}""");

        var merged = MeshOperations.MergePatch(target, patch)!.AsObject();
        merged["a"]!.GetValue<int>().Should().Be(2);

        target.AsObject()["a"]!.GetValue<int>().Should().Be(1); // target left untouched
        Assert.False(target.AsObject().ContainsKey("b"));
        patch.AsObject()["a"]!.GetValue<int>().Should().Be(2);  // patch left untouched
    }
}
