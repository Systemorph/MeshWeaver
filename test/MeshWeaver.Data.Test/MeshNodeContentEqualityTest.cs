using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the sync fan-out amplifier behind <see cref="MeshNode"/> value equality.
///
/// <para><see cref="MeshNode.Content"/> is an <c>object?</c> that, on the
/// <see cref="IMeshNodeStreamCache"/> / cross-hub-sync side, is a
/// <see cref="JsonElement"/> (the cache hub does not know domain types, so Content
/// lands there as raw JSON). A <c>JsonElement</c> has NO structural equality — two
/// elements with identical JSON are never <c>.Equals</c> — so the compiler-synthesized
/// record equality reported EVERY re-synced node as "changed". That defeated
/// <c>SynchronizationStream.SetCurrent</c>'s value-equality dedup, which re-broadcast
/// the whole node on every push (a single thread node took ~130 <c>SetCurrentRequest</c>s
/// for one streamed round — the O(threads²) sync fan-out under a shared mesh).</para>
///
/// <para><see cref="MeshNode.Equals(MeshNode)"/> now compares <c>JsonElement</c> content
/// with <see cref="JsonElement.DeepEquals(JsonElement, JsonElement)"/>, so an identical
/// re-synced node compares equal and the dedup fires.</para>
/// </summary>
public class MeshNodeContentEqualityTest
{
    private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    [Fact]
    public void JsonElementContent_StructurallyEqual_DistinctInstances_AreEqual()
    {
        // Two independently-parsed JsonElements with identical JSON — distinct instances,
        // exactly the shape the cache holds after a sync round-trip.
        var a = new MeshNode("t1", "TestData/_Thread")
        {
            NodeType = "Thread",
            Content = Json("""{"messages":["m1","m2"],"status":"Executing"}""")
        };
        var b = new MeshNode("t1", "TestData/_Thread")
        {
            NodeType = "Thread",
            Content = Json("""{"messages":["m1","m2"],"status":"Executing"}""")
        };

        // Pre-fix: FALSE (JsonElement struct equality) → SetCurrent re-emits forever.
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void JsonElementContent_DifferentJson_AreNotEqual()
    {
        var a = new MeshNode("t1", "ns") { Content = Json("""{"status":"Executing"}""") };
        var b = new MeshNode("t1", "ns") { Content = Json("""{"status":"Idle"}""") };

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void JsonElementContent_MemberOrderDiffers_StillEqual()
    {
        // DeepEquals is order-insensitive for object members — a re-serialised node whose
        // keys land in a different order must still dedup.
        var a = new MeshNode("t1", "ns") { Content = Json("""{"a":1,"b":2}""") };
        var b = new MeshNode("t1", "ns") { Content = Json("""{"b":2,"a":1}""") };

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void ScalarFieldDiffers_NotEqual_EvenWithEqualJsonContent()
    {
        var content = """{"x":1}""";
        var a = new MeshNode("t1", "ns") { Version = 1, Content = Json(content) };
        var b = new MeshNode("t1", "ns") { Version = 2, Content = Json(content) };

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void RuntimeOnlyHubConfiguration_ExcludedFromEquality()
    {
        // [JsonIgnore]/[NotMapped] runtime wiring is reference-typed and not part of the
        // node's persisted value; two delegate instances must NOT make nodes unequal.
        var a = new MeshNode("n", "ns") { NodeType = "X", HubConfiguration = c => c };
        var b = new MeshNode("n", "ns") { NodeType = "X", HubConfiguration = c => c };

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void TypedContent_UnchangedBehavior_StillCompares()
    {
        var a = new MeshNode("n", "ns") { Content = "hello" };
        var b = new MeshNode("n", "ns") { Content = "hello" };
        Assert.True(a.Equals(b));

        var c = new MeshNode("n", "ns") { Content = "world" };
        Assert.False(a.Equals(c));
    }

    [Fact]
    public void NullContent_BothNull_Equal_OneNull_NotEqual()
    {
        var a = new MeshNode("n", "ns") { Content = null };
        var b = new MeshNode("n", "ns") { Content = null };
        Assert.True(a.Equals(b));

        var c = new MeshNode("n", "ns") { Content = Json("""{"x":1}""") };
        Assert.False(a.Equals(c));
    }

    [Fact]
    public void EntityStoreChain_WithJsonElementNodes_DedupesViaValueEquality()
    {
        // The full SetCurrent dedup chain: EntityStore -> InstanceCollection -> MeshNode.
        // Two independently-built stores holding a JsonElement-content node must compare
        // equal so a re-synced identical store does NOT re-emit (the amplifier).
        MeshNode Node() => new("t1", "TestData/_Thread")
        {
            NodeType = "Thread",
            Content = Json("""{"messages":["m1"],"pending":{}}""")
        };

        var store1 = new EntityStore(new Dictionary<string, InstanceCollection>
        {
            ["MeshNode"] = new InstanceCollection(new[] { (object)Node() }, n => ((MeshNode)n).Path)
        });
        var store2 = new EntityStore(new Dictionary<string, InstanceCollection>
        {
            ["MeshNode"] = new InstanceCollection(new[] { (object)Node() }, n => ((MeshNode)n).Path)
        });

        Assert.True(store1.Equals(store2));
    }
}
