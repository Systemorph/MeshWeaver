#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the STORE-level half of "a node's durable state never moves backward" (issue #971) — the half
/// <see cref="MonotonicWriteGuardWindowTest"/> deliberately does not cover.
///
/// <para><b>The gap.</b> <c>MonotonicWriteGuardStorageAdapter</c>'s high-water mark is per-PROCESS. A
/// second replica starts with an EMPTY mark table, so its first write to a given path finds no mark,
/// takes the unverified fast path, and lands unchecked. Nothing below it objected either: the Postgres
/// upsert carried no version condition and the in-memory store was a plain
/// <c>_nodes[path] = node</c>. So on a fresh replica's first write to a path, monotonicity was enforced
/// by NOTHING — not the empty mark, not the store. Invisible in single-process tests, live in any
/// rollout, KEDA scale-out or restart, and silent when it fires: the stale write is ACKED and the next
/// write is minted one above the rolled-back row, so the wrong lineage persists.</para>
///
/// <para><b>How the property is constructed rather than raced.</b> "A second writer with no prior
/// mark" is built directly — a fresh <c>MonotonicWriteGuardStorageAdapter</c> instance over the SAME
/// underlying store, which is exactly what a newly started replica holds. No load, no sleeps, no
/// timing budget: the empty mark is the initial state of a new instance.</para>
///
/// <para><b>Fail-without</b> (verified against current <c>main</c>): the fresh guard finds no mark,
/// hands the stale node straight to a store with no version condition, and the durable row becomes the
/// v1 snapshot — both the emission assertion and the durable-read assertion fail.
/// <b>Pass-with:</b> the store itself refuses the regressing write, hands back the row it kept, and the
/// guard merges the loser into durable truth instead of bouncing a conflict at a caller that has no
/// retry loop.</para>
/// </summary>
public class StoreLevelWriteConflictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>Reads the node straight out of durable storage — no hub, no stream, no cache.</summary>
    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    /// <summary>
    /// A SIMULATED SECOND REPLICA: a fresh write-integrity guard over the same store this process
    /// already holds. Its high-water table is empty — precisely the state a newly started pod is in
    /// for every path it has not yet touched — so it exercises the case where the in-process filter
    /// cannot help and only the store can.
    /// </summary>
    private IStorageAdapter FreshReplicaGuard()
        => new MonotonicWriteGuardStorageAdapter(
            Mesh.ServiceProvider.GetRequiredKeyedService<IStorageAdapter>("inner"),
            Mesh.ServiceProvider.GetService<ILogger<MonotonicWriteGuardStorageAdapter>>());

    [Fact(Timeout = 55_000)]
    public async Task AWriterWithNoHighWaterMark_CannotRollTheDurableRowBack()
    {
        var id = $"store-cas-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        created!.Version.Should().Be(1, "the create is revision 1 — the stale snapshot below rides that version");

        const long durableVersion = 7000L;
        await Storage.Write(created with { Name = "durable-advance", Version = durableVersion }, JsonOptions)
            .Should().Within(30.Seconds()).Emit();

        // The stale snapshot a lagging replica still holds: the create-time node, unchanged Version.
        // Presented through a guard that has NEVER seen this path, so its mark table is empty and the
        // only thing that can stop it is the store.
        var replica = FreshReplicaGuard();
        var emitted = await replica.Write(created with { Name = "stale-snapshot" }, JsonOptions)
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[replica write] emitted={(emitted is null ? "null" : $"v{emitted.Version}/{emitted.Name}")}");

        emitted.Should().NotBeNull();
        emitted!.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "a refused write must emit the STORED (winning) row — that is the 'what is durable now' "
            + "contract the write-integrity chain merges into, and the signal an owner rebases on");

        var durable = await ReadDurable(path).Should().Within(30.Seconds()).Emit();
        durable.Should().NotBeNull();
        durable!.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "MeshNode.Version is the node's forward-only revision counter, so a write below the durable "
            + "row is a stale snapshot — and on a replica whose in-process high-water mark is empty the "
            + "STORE is the only thing left to enforce that");
        durable.Name.Should().Be("durable-advance",
            "the newer row's content must survive a losing write, not be overwritten by it");
    }

    [Fact(Timeout = 55_000)]
    public async Task AConflictIsMerged_NotRefused_AndTheDroppedValueReachesTheActivityLog()
    {
        var id = $"store-merge-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active,
            Description = "alpha beta"
        }).Should().Within(30.Seconds()).Emit();

        var created = await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });
        var staleVersion = created!.Version;

        const long durableVersion = 9000L;
        await Storage.Write(
                created with { Name = "durable-advance", Description = "alpha beta", Version = durableVersion },
                JsonOptions)
            .Should().Within(30.Seconds()).Emit();

        // The losing write. Two members diverge in DIFFERENT ways on purpose:
        //   Name        — "durable-advance" vs "stale-name": the splice both removes and inserts, so
        //                 with no common ancestor it is NOT auto-resolvable → latest wins, and the drop
        //                 must be reported.
        //   Description — "alpha beta" vs "alpha beta gamma": a pure insertion, so the stale text is a
        //                 superset → both writers' content survives.
        var emitted = await Storage.Write(
                created with { Name = "stale-name", Description = "alpha beta gamma", Version = staleVersion },
                JsonOptions)
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"[conflicting write] emitted={(emitted is null ? "null" : $"v{emitted.Version}/{emitted.Name}")}");

        var durable = await ReadDurable(path).Should().Within(30.Seconds()).Emit();
        durable.Should().NotBeNull();
        Output.WriteLine($"[durable] v{durable!.Version} name={durable.Name} description={durable.Description}");

        durable.Version.Should().BeGreaterThanOrEqualTo(durableVersion,
            "resolving a conflict by merging must never move the row backward — the merge keeps the "
            + "LATEST row's version precisely so re-persisting it cannot itself read as a rollback");
        durable.Name.Should().Be("durable-advance",
            "two divergent strings with no common ancestor are not auto-resolvable — latest wins");
        durable.Description.Should().Be("alpha beta gamma",
            "the losing write's text was a pure insertion, so both writers' content survives — this is "
            + "the 'merge both objects together' half of the policy, and refusing the write outright "
            + "would have destroyed it");

        // 🚨 The load-bearing half. Latest-wins is acceptable; latest-wins INVISIBLY is the defect
        // (#968 was an acked write that rolled a row back with no error anywhere). The dropped member
        // must leave a durable, user-visible trace.
        // Keyed on the DURABLE version alone — one record per durable revision, so a wedged writer
        // retrying at an ever-increasing version cannot litter one satellite per attempt.
        var activityPath = $"{path}/_Activity/write-conflict-{durableVersion}";
        var activity = await ReadDurable(activityPath).Should().Within(30.Seconds()).Emit();
        activity.Should().NotBeNull(
            "a conflict whose resolution DROPPED a value must record it in the activity log — a "
            + "resolution nobody can see is the failure mode this whole mechanism exists to stop");
        // 🚨 "Activity" — `ActivityNodeType.NodeType`, the ONLY registered activity node type.
        // "ActivityLog" is the CLR *content* type's TypeRegistry discriminator
        // (`DataExtensions`: `WithType(typeof(ActivityLog), nameof(ActivityLog))`), never a node
        // type: nothing calls `AddMeshNodes` with it, so a node stamped "ActivityLog" has no node-
        // type definition at all — no views, no satellite access rule — and, the point here, it
        // matches no `nodeType:Activity` query, which is what `RunningActivitiesStripe` and the
        // activity feed run. A record that exists but that no feed can surface fails the very
        // requirement asserted three lines above: a resolution nobody can see.
        activity!.NodeType.Should().Be(ActivityNodeType.NodeType,
            "a write-conflict record is only 'visible' if the activity feed can find it, and every "
            + "feed query filters nodeType:Activity — the path landing under _Activity is necessary "
            + "but not sufficient");

        var log = activity.Content.Should().BeOfType<ActivityLog>().Subject;
        log.Category.Should().Be(ActivityCategory.WriteConflict);
        log.Status.Should().Be(ActivityStatus.Warning,
            "a resolution that dropped a value is a warning, not a clean success");
        log.Messages.Where(m => m.LogLevel == LogLevel.Warning)
            .Should().Contain(m => m.Message.Contains(nameof(MeshNode.Name), StringComparison.Ordinal),
                "the entry must NAME the member whose value was dropped, not merely say a conflict happened");
    }
}
