using System.Collections.Generic;
using System.Text.Json.Nodes;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro + spec for <see cref="MeshNodePatchMerge"/> — the owner-side three-way merge that
/// kills the reordered/stale cross-hub-patch flap. A patch computed against a stale base must never flap a
/// SCALAR field back to an older value (the compile-heavy NodeType wedge: a flapped Status/RequestedReleaseAt
/// makes the release watcher skip the recompile → the overview never settles), while disjoint STRING edits
/// must merge instead of clobbering.
/// </summary>
public class MeshNodePatchMergeTest
{
    private static JsonObject Obj(params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields)
            o[k] = v;
        return o;
    }

    private static List<string> Merge(JsonObject live, JsonObject patch, JsonObject? baseValues)
    {
        var refused = new List<string>();
        MeshNodePatchMerge.Apply(live, patch, baseValues, refused.Add);
        return refused;
    }

    [Fact]
    public void FastPath_NoInterveningChange_AppliesWriterValue()
    {
        // live == base: the writer's value wins, byte-identical to plain RFC 7396.
        var live = Obj(("Status", "Compiling"), ("Name", "hello"));
        var @base = Obj(("Status", "Compiling"), ("Name", "hello"));
        var patch = Obj(("Status", "Ok"), ("Name", "hello world"));

        var refused = Merge(live, patch, @base);

        Assert.Empty(refused);
        Assert.Equal("Ok", live["Status"]!.GetValue<string>());
        Assert.Equal("hello world", live["Name"]!.GetValue<string>());
    }

    [Fact]
    public void ScalarConflict_StalePatch_RefusesAndKeepsLive_NoFlap()
    {
        // The flap: live advanced to 665; a reordered/stale patch (base=2) tries to set 399.
        // Last-write-wins would flap to 399; the three-way merge REFUSES → live stays 665.
        var live = Obj(("RequestedReleaseAt", 665));
        var @base = Obj(("RequestedReleaseAt", 2));
        var patch = Obj(("RequestedReleaseAt", 399));

        var refused = Merge(live, patch, @base);

        Assert.Contains("RequestedReleaseAt", refused);
        Assert.Equal(665, live["RequestedReleaseAt"]!.GetValue<int>());
    }

    [Fact]
    public void BoolAndEnumConflict_Refused()
    {
        // Non-monotonic scalars (IsDirty bool, Status enum-as-string) flap under reorder too → refuse.
        var live = Obj(("IsDirty", false), ("Status", "Ok"));
        var @base = Obj(("IsDirty", true), ("Status", "Compiling"));
        var patch = Obj(("IsDirty", true), ("Status", "Error"));

        var refused = Merge(live, patch, @base);

        Assert.Contains("IsDirty", refused);
        Assert.Contains("Status", refused);
        Assert.False(live["IsDirty"]!.GetValue<bool>());
        Assert.Equal("Ok", live["Status"]!.GetValue<string>());
    }

    [Fact]
    public void StringConflict_DisjointEdits_BothMerge()
    {
        // base "hello world"; live uppercased word 1; writer (stale base) uppercased word 2.
        // Disjoint splices → both land → "HELLO WORLD".
        var live = Obj(("Name", "HELLO world"));
        var @base = Obj(("Name", "hello world"));
        var patch = Obj(("Name", "hello WORLD"));

        var refused = Merge(live, patch, @base);

        Assert.Empty(refused);
        Assert.Equal("HELLO WORLD", live["Name"]!.GetValue<string>());
    }

    [Fact]
    public void StringConflict_OverlappingEdits_KeepsNewerLive()
    {
        // Both sides rewrote the same middle char → overlap → resolve-by-version: keep live.
        var live = Obj(("Name", "aXc"));
        var @base = Obj(("Name", "abc"));
        var patch = Obj(("Name", "aYc"));

        var refused = Merge(live, patch, @base);

        Assert.Contains("Name", refused);
        Assert.Equal("aXc", live["Name"]!.GetValue<string>());
    }

    [Fact]
    public void NestedContent_StringMergesAndScalarRefuses()
    {
        // The real shape: MeshNode.Content carries a string (Text) and a scalar (Version).
        var live = Obj(("Content", Obj(("Text", "HELLO world"), ("Order", 665))));
        var @base = Obj(("Content", Obj(("Text", "hello world"), ("Order", 2))));
        var patch = Obj(("Content", Obj(("Text", "hello WORLD"), ("Order", 399))));

        var refused = Merge(live, patch, @base);

        var content = (JsonObject)live["Content"]!;
        Assert.Equal("HELLO WORLD", content["Text"]!.GetValue<string>());
        Assert.Equal(665, content["Order"]!.GetValue<int>());
        Assert.Contains("Order", refused);
    }

    [Fact]
    public void NoBaseCarried_FallsBackToLastWriteWins()
    {
        // No base values (legacy sender / writer-added field) → no conflict signal → apply patch.
        var live = Obj(("Status", "Ok"));
        var patch = Obj(("Status", "Error"));

        var refused = Merge(live, patch, baseValues: null);

        Assert.Empty(refused);
        Assert.Equal("Error", live["Status"]!.GetValue<string>());
    }

    [Fact]
    public void ExtractBaseValues_MirrorsPatchLeaves()
    {
        var baseNode = Obj(("Status", "Compiling"), ("Name", "hello"), ("Untouched", "x"));
        var patch = Obj(("Status", "Ok"), ("Name", "hello world"));

        var extracted = MeshNodePatchMerge.ExtractBaseValues(baseNode, patch);

        Assert.NotNull(extracted);
        Assert.Equal("Compiling", extracted!["Status"]!.GetValue<string>());
        Assert.Equal("hello", extracted["Name"]!.GetValue<string>());
        Assert.False(extracted.ContainsKey("Untouched"), "only changed leaves carry a base value");
    }
}
