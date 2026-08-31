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

    // ── Array conflicts: the inbox-drop bug. A cross-hub write of an ARRAY field (the thread inbox
    //    PendingUserMessages / IngestedMessageIds) against a stale base used to hit the blanket scalar
    //    REFUSE and silently lose the writer's submission (RapidSubmits_PileUpAndAllIngest /
    //    OrleansResubmitDeadlock). Arrays now three-way merge by element identity. ──

    private static JsonArray Arr(params JsonNode?[] elems)
    {
        var a = new JsonArray();
        foreach (var e in elems) a.Add(e);
        return a;
    }

    private static List<string> Strings(JsonNode? arr)
    {
        var list = new List<string>();
        foreach (var e in (JsonArray)arr!) list.Add(e!.GetValue<string>());
        return list;
    }

    [Fact]
    public void ArrayConflict_WriterAddition_LandsOnDrainedLive()
    {
        // The owner DRAINED m1 (live=[]); the writer (a new chat) ADDS m2 against a stale base ([m1]).
        // The old REFUSE kept live=[] → m2 silently dropped. The merge must land m2, NOT re-add drained m1.
        var live = Obj(("Pending", Arr()));
        var @base = Obj(("Pending", Arr("m1")));
        var patch = Obj(("Pending", Arr("m1", "m2")));

        var refused = Merge(live, patch, @base);

        Assert.DoesNotContain("Pending", refused);
        Assert.Equal(new[] { "m2" }, Strings(live["Pending"]));
    }

    [Fact]
    public void ArrayConflict_ConcurrentAdditions_BothLand()
    {
        // Owner appended "b", writer appended "c" off the same base — both additions must survive.
        var live = Obj(("Ingested", Arr("a", "b")));
        var @base = Obj(("Ingested", Arr("a")));
        var patch = Obj(("Ingested", Arr("a", "c")));

        var refused = Merge(live, patch, @base);

        Assert.DoesNotContain("Ingested", refused);
        Assert.Equal(new[] { "a", "b", "c" }, Strings(live["Ingested"]));
    }

    [Fact]
    public void ArrayConflict_WriterRemoval_Applied()
    {
        // The writer removed "b" (patch=[a]); the owner hadn't (live=[a,b]). The removal must apply.
        var live = Obj(("Items", Arr("a", "b")));
        var @base = Obj(("Items", Arr("a", "b")));
        var patch = Obj(("Items", Arr("a")));

        var refused = Merge(live, patch, @base);

        Assert.DoesNotContain("Items", refused);
        Assert.Equal(new[] { "a" }, Strings(live["Items"]));
    }

    [Fact]
    public void ArrayConflict_ObjectElements_DrainedThenAppended()
    {
        // PendingUserMessages are OBJECTS — identity is whole-element value equality, so a drained element
        // is not resurrected and the writer's new one lands.
        static JsonNode M(string id) => new JsonObject { ["id"] = id };
        var live = Obj(("Pending", Arr()));                 // owner already drained M("m1")
        var @base = Obj(("Pending", Arr(M("m1"))));
        var patch = Obj(("Pending", Arr(M("m1"), M("m2"))));

        var refused = Merge(live, patch, @base);

        Assert.DoesNotContain("Pending", refused);
        var pending = (JsonArray)live["Pending"]!;
        Assert.Single(pending);
        Assert.Equal("m2", pending[0]!["id"]!.GetValue<string>());
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // A LEAF THE WRITER AND THE OWNER ALREADY AGREE ON IS NOT A CONFLICT
    //
    // `live != base` says the owner moved since the writer read it. It does NOT say the two
    // DISAGREE — and when the patch value already equals the live value, applying it is a no-op,
    // so refusing it invents a conflict. That invention is not free: a refusal NACKs Conflict
    // (partial refusals included, #2463/#2840), Conflict's remedy is to re-run the caller's update
    // lambda, and a RELATIVE mutation re-run against a node that has moved on applies twice.
    // Measured on MeshWeaver.Plugins#1009 — a chat resubmit refused three times on three fields it
    // had set to exactly the owner's own values, then re-run three times, losing message cells.
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NoOpLeaf_PatchAlreadyEqualsLive_NotRefused()
    {
        // The writer computed "Status: Idle" off a stale base that still said Executing.
        // The owner is ALREADY Idle. Nothing to resolve; nothing to refuse.
        var live = Obj(("Status", "Idle"));
        var @base = Obj(("Status", "Executing"));
        var patch = Obj(("Status", "Idle"));

        var refused = Merge(live, patch, @base);

        Assert.Empty(refused);
        Assert.Equal("Idle", live["Status"]!.GetValue<string>());
    }

    [Fact]
    public void NoOpLeaf_RemovalOfAnAlreadyRemovedKey_NotRefused()
    {
        // The commonest shape of all, and the exact #1009 trigger: an RFC 7396 REMOVAL
        // (patch value null) of a key the owner has already removed. ActiveMessageId /
        // ExecutionStartedAt were cleared by the round that finished; the resubmit patch
        // clears them too.
        var live = Obj(("Name", "n"));                                  // ActiveMessageId absent
        var @base = Obj(("ActiveMessageId", "cell-1"), ("Name", "n"));
        var patch = Obj(("ActiveMessageId", null));

        var refused = Merge(live, patch, @base);

        Assert.Empty(refused);
        Assert.False(live.ContainsKey("ActiveMessageId"));
    }

    [Fact]
    public void NoOpLeaf_IdenticalString_NotRefused()
    {
        // Checked BEFORE the string rebase: an identical string needs no splice, and the
        // overlapping-delta arm would otherwise refuse it.
        var live = Obj(("Text", "the same text"));
        var @base = Obj(("Text", "something entirely different"));
        var patch = Obj(("Text", "the same text"));

        var refused = Merge(live, patch, @base);

        Assert.Empty(refused);
        Assert.Equal("the same text", live["Text"]!.GetValue<string>());
    }

    [Fact]
    public void NoOpLeaf_DoesNotMaskAGenuineScalarConflict()
    {
        // The guard is exact: the moment the values differ, the flap guard is back in force.
        var live = Obj(("Status", "Ok"));
        var @base = Obj(("Status", "Compiling"));
        var patch = Obj(("Status", "Error"));

        var refused = Merge(live, patch, @base);

        Assert.Contains("Status", refused);
        Assert.Equal("Ok", live["Status"]!.GetValue<string>());
    }
}
