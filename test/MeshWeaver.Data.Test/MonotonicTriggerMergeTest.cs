using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic repro + spec for the owner-side MONOTONIC-TRIGGER merge
/// (<see cref="DataExtensions.RebaseMonotonicTriggers"/> composed into
/// <see cref="DataExtensions.ApplyMeshNodeMerge"/> — the exact seam the
/// <c>PatchDataRequest</c> handler runs for a cross-hub MeshNode write).
///
/// <para>Incident pinned (memex-cloud 2026-07-20, Store/Plugin): during a GitSync burst every
/// mirror's cached base lags the owner, so an EXPLICIT compile trigger — a cross-hub
/// <c>stream.Update</c> stamping a NEW (strictly larger) <c>RequestedReleaseAt</c> — arrived
/// with <c>base ≠ live</c>. The generic three-way merge treats every conflicting scalar as
/// non-mergeable and REFUSES it, keeping the live value: the user's compile request was
/// silently LOST ("[MergeGuard] refused stale/reordered cross-hub write"), and the stuck
/// NodeType could never be re-triggered remotely. For the contractually strictly-increasing
/// trigger the conflict IS mergeable — the newest instant wins — so the merge must land a
/// FORWARD trigger (rebasing the writer's stale base) while still dropping a BACKWARD (flapped)
/// one and still refusing every other conflicting scalar.</para>
/// </summary>
public class MonotonicTriggerMergeTest
{
    private static readonly JsonSerializerOptions Options = new();

    private const string HubPath = "Store/Plugin";

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-07-20T21:40:00Z");
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-07-20T21:41:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-07-20T21:43:00Z");
    private static readonly DateTimeOffset T3 = DateTimeOffset.Parse("2026-07-20T21:45:00Z");

    private static JsonObject Node(params (string Key, JsonNode? Value)[] contentFields)
    {
        var content = new JsonObject();
        foreach (var (k, v) in contentFields)
            content[k] = v;
        return new JsonObject { ["Content"] = content };
    }

    private static JsonNode Instant(DateTimeOffset at) => JsonValue.Create(at.ToString("o"));

    private static JsonObject Merge(JsonObject live, JsonObject patch, JsonObject baseValues)
    {
        var message = new PatchDataRequest(new MeshNodeReference(), new RawJson(patch.ToJsonString(Options)))
        {
            BaseValues = new RawJson(baseValues.ToJsonString(Options))
        };
        DataExtensions.ApplyMeshNodeMerge(
            live, patch, isMeshNode: true, message, Options, logger: null, HubPath);
        return live;
    }

    private static DateTimeOffset ReadTrigger(JsonObject live) =>
        DateTimeOffset.Parse(live["Content"]!["RequestedReleaseAt"]!.GetValue<string>());

    /// <summary>
    /// THE incident case: the writer's mirror lagged the owner (base=T1 while live already
    /// advanced to T2), and the writer stamps a genuinely NEWER trigger T3. The generic
    /// scalar-conflict refusal silently swallowed T3 → the explicit compile trigger produced no
    /// compile, ever. The monotonic merge must land T3: a strictly-increasing trigger merges by
    /// "newest instant wins" regardless of the writer's base.
    /// </summary>
    [Fact]
    public void ForwardTrigger_OverStaleBase_LandsNewerTrigger()
    {
        var live = Node(("RequestedReleaseAt", Instant(T2)));
        var @base = Node(("RequestedReleaseAt", Instant(T1)));
        var patch = Node(("RequestedReleaseAt", Instant(T3)));

        Merge(live, patch, @base);

        Assert.Equal(T3, ReadTrigger(live));
    }

    /// <summary>
    /// The original flap guard stays intact: a reordered/stale patch carrying an OLDER trigger
    /// (T0 &lt; live T2) must never move the trigger backward — the live value is kept.
    /// </summary>
    [Fact]
    public void BackwardTrigger_OverStaleBase_KeepsLive()
    {
        var live = Node(("RequestedReleaseAt", Instant(T2)));
        var @base = Node(("RequestedReleaseAt", Instant(T1)));
        var patch = Node(("RequestedReleaseAt", Instant(T0)));

        Merge(live, patch, @base);

        Assert.Equal(T2, ReadTrigger(live));
    }

    /// <summary>
    /// Unchanged-since-base fast path is untouched: base == live ⇒ the writer's forward value
    /// applies exactly as before (no conflict, no rebase needed).
    /// </summary>
    [Fact]
    public void ForwardTrigger_CleanBase_AppliesUnchanged()
    {
        var live = Node(("RequestedReleaseAt", Instant(T2)));
        var @base = Node(("RequestedReleaseAt", Instant(T2)));
        var patch = Node(("RequestedReleaseAt", Instant(T3)));

        Merge(live, patch, @base);

        Assert.Equal(T3, ReadTrigger(live));
    }

    /// <summary>
    /// The monotonic exception is scoped to the ONE contractually strictly-increasing trigger.
    /// Every other conflicting scalar (here CompilationStatus — the compile single-flight
    /// state) keeps the REFUSE semantics: a stale-based cross-hub status write must never
    /// clobber the owner's live state (that protection is what keeps a stale mirror from
    /// flipping Compiling back to Pending mid-compile).
    /// </summary>
    [Fact]
    public void NonMonotonicScalarConflict_StillRefused()
    {
        var live = Node(("CompilationStatus", JsonValue.Create("Compiling")),
            ("RequestedReleaseAt", Instant(T2)));
        var @base = Node(("CompilationStatus", JsonValue.Create("Ok")),
            ("RequestedReleaseAt", Instant(T1)));
        var patch = Node(("CompilationStatus", JsonValue.Create("Pending")),
            ("RequestedReleaseAt", Instant(T3)));

        Merge(live, patch, @base);

        Assert.Equal("Compiling", live["Content"]!["CompilationStatus"]!.GetValue<string>());
        // …while the monotonic trigger in the SAME patch still lands.
        Assert.Equal(T3, ReadTrigger(live));
    }
}
