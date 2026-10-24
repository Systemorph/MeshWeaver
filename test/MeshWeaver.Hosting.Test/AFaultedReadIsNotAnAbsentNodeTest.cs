using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A read that FAILED must not answer as a node that does not EXIST</b> (issue #1186).
///
/// <para><b>The gap this closes.</b> <c>StorageAdapterMeshQueryProvider</c> catches a store fault,
/// logs it at Warning, drops the rows it could not get and emits its <c>Initial</c> over what
/// survived. Keeping the survivors is deliberate — one corrupt row must not kill a listing — but
/// the frame then claimed a COMPLETE answer. So the provider was never recorded in
/// <see cref="QueryResultChange{T}.SilentProviders"/>, every consumer that refuses to read absence
/// off a floor could not fire, and the gap travelled outward as a fact: path resolution emitted
/// <c>null</c> and <c>MessageHubGrain</c> reported <i>"Either the node does not exist or no query
/// provider claims its partition"</i> over a node that was present and a provider that had
/// answered. Both clauses were measured false on #1186 — three of three failing paths read back
/// present on the live mesh, and the provider behind the gap had claimed the partition.</para>
///
/// <para><b>The other half already existed.</b> <c>SilentProviders</c> (MeshWeaver#4557) names a
/// provider that produced NO Initial, and <c>PathResolutionService</c> already refuses to resolve
/// from a frame carrying one. A provider that produces a SHORTER Initial is the same lie with a
/// frame attached — so the fix is one provider-side verdict,
/// <see cref="QueryResultChange{T}.SnapshotIncomplete"/>, folded into that same field by the
/// aggregator. Nothing downstream changes.</para>
///
/// <para><b>Why both cases are here.</b> A test that only drives the faulting adapter cannot see a
/// rule that CHOOSES: an implementation that stamped every frame incomplete would pass it, and
/// would refuse every genuinely absent node in production — the opposite defect, and a far worse
/// one. So the healthy adapter asking for a path that genuinely is not there is a case of this
/// test, not a separate concern: it is the side of the change that must NOT move.</para>
///
/// <para>Deterministic by construction — the fake store answers synchronously and the fault is
/// injected by path, so no timing decides any case.</para>
/// </summary>
public class AFaultedReadIsNotAnAbsentNodeTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path, string nodeType = "Markdown") => new(
        path.Split('/').Last(),
        path.Contains('/') ? path[..path.LastIndexOf('/')] : null)
    {
        Name = path.Split('/').Last(),
        NodeType = nodeType,
        State = MeshNodeState.Active,
    };

    /// <summary>
    /// The exact-path probe path resolution takes: <c>path:a|a/b scope:exact</c>, which the provider
    /// serves through <c>IStorageAdapter.ReadMany</c> — the call the #1186 chain fails on.
    /// </summary>
    private static MeshQueryRequest ExactProbe(params string[] paths) =>
        new() { Query = $"path:{string.Join("|", paths)} scope:exact" };

    private static async Task<QueryResultChange<MeshNode>> FirstFrame(
        IMeshQueryCore query, MeshQueryRequest request, CancellationToken cancellationToken) =>
        await query.Query<MeshNode>(request, Options)
            .FirstAsync()
            // TestTimeouts rather than a literal: nothing here is expected to WAIT, so this bound
            // exists only so a regression that wedges the merge fails instead of hanging the shard.
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);

    /// <summary>
    /// 🚨 THE #1186 REPRO. The store throws on the read that would have found the node. The frame
    /// must say so — and it must say so in the field the existing consumers read, not only in a log
    /// line nobody downstream can see.
    ///
    /// <para>Note what is asserted and what is not: the surviving row is still served. This change
    /// does not make a partial read fail; it makes a partial read <i>admit</i> that it is partial.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AStoreThatThrowsOnTheRead_ProducesAFrameThatNamesItsProvider_NotAnEmptyAnswer()
    {
        var store = new InMemoryStorageAdapter();
        await store.Write(Node("Ifrs17", "Group"), Options).Await(TestContext.Current.CancellationToken);
        await store.Write(Node("Ifrs17/AocType"), Options).Await(TestContext.Current.CancellationToken);

        // The production shape of a Postgres connect timeout on one path of a multi-path probe: the
        // READ legs (Read, and a ReadMany naming it — which faults as a whole) of that path throw.
        var failing = new FaultInjectingStorageAdapter(store);
        var fault = failing.FailOperations(p => p.Contains("AocType", StringComparison.OrdinalIgnoreCase),
            StorageLegs.Read, description: "paths containing AocType");
        var provider = new StorageAdapterMeshQueryProvider(persistence: failing);
        var query = (IMeshQueryCore)new MeshQuery([provider], hub: null!);

        var frame = await FirstFrame(query, ExactProbe("Ifrs17", "Ifrs17/AocType"),
            TestContext.Current.CancellationToken);

        fault.ArrivalCount.Should().BeGreaterThan(0,
            "the fault must actually have been INJECTED — without this the test could pass having "
            + "exercised the healthy path, which is the shape of a verification that cannot fail");

        frame.SnapshotIncomplete.Should().BeTrue(
            "the provider caught a store fault and dropped the rows behind it, so its snapshot is "
            + "short of what the query asked for and the frame has to carry that verdict");

        (frame.SilentProviders ?? []).Should().NotBeEmpty(
            "SilentProviders is the field every existing consumer reads to refuse absence off a "
            + "floor (PathResolutionService, the plugin gate's grant read, MeshNodeStreamCache's "
            + "no-cache rule) — a verdict the aggregator does not fold into it reaches nobody");
        frame.SilentProviders!.Should().Contain(((IMeshQueryProvider)provider).Name,
            "naming the provider is the whole difference between 'look for the node' and 'look for "
            + "the store' — an unnamed floor sends the reader to the one question that is not in doubt");
    }

    /// <summary>
    /// 🚨 THE SIDE THAT MUST NOT MOVE. The same query shape, the same provider, an adapter that
    /// works — and a path that genuinely is not stored. That is a DETERMINATE absence and must stay
    /// one: it is what lets <c>MessageHubGrain</c> refuse a hub promptly instead of burning a budget,
    /// and what lets a resolution be cached at all.
    ///
    /// <para>Without this case the fix is unfalsifiable: stamping every frame incomplete passes the
    /// test above and turns every missing node into a refusal.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AHealthyStoreMissingThePath_StaysADeterminateAbsence()
    {
        var store = new InMemoryStorageAdapter();
        await store.Write(Node("Ifrs17", "Group"), Options).Await(TestContext.Current.CancellationToken);

        // The same adapter with nothing armed — which is what makes it this test's control.
        var healthy = new FaultInjectingStorageAdapter(store);
        var provider = new StorageAdapterMeshQueryProvider(persistence: healthy);
        var query = (IMeshQueryCore)new MeshQuery([provider], hub: null!);

        var frame = await FirstFrame(query, ExactProbe("Ifrs17", "Ifrs17/NeverWritten"),
            TestContext.Current.CancellationToken);


        frame.SnapshotIncomplete.Should().BeFalse(
            "every read behind this frame completed; the path is simply not there. Marking this "
            + "incomplete would turn every absent node into a refusal — the opposite defect, and "
            + "the one that would stall every per-node hub activation in the mesh");
        (frame.SilentProviders ?? []).Should().BeEmpty(
            "a complete snapshot may be read as a real answer and cached like one");
        frame.Items.Select(n => n.Path).Should().Contain("Ifrs17",
            "the row that IS there must still be served — otherwise this control proves nothing "
            + "about the query having run");
    }

    /// <summary>
    /// The walk's half of the same rule. A scope walk whose <c>ListChildPaths</c> throws yields a
    /// SHORTER subtree, and a consumer reading "this subtree has no such child" off it is reading a
    /// store fault. Separate from the exact-path case because they are separate catches in the
    /// provider, and a fix that threads the verdict through one and not the other reads as complete.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AWalkThatThrows_AlsoMarksItsSnapshotIncomplete()
    {
        var store = new InMemoryStorageAdapter();
        await store.Write(Node("Ifrs17", "Group"), Options).Await(TestContext.Current.CancellationToken);
        await store.Write(Node("Ifrs17/AocType"), Options).Await(TestContext.Current.CancellationToken);

        // Every subtree enumeration (ListChildPaths, ListDescendantPaths) throws.
        var failing = new FaultInjectingStorageAdapter(store);
        var fault = failing.FailOperations(_ => true, StorageLegs.Enumerate, description: "every path");
        var provider = new StorageAdapterMeshQueryProvider(persistence: failing);
        var query = (IMeshQueryCore)new MeshQuery([provider], hub: null!);

        var frame = await FirstFrame(query,
            new MeshQueryRequest { Query = "path:Ifrs17 scope:descendants nodeType:Markdown" },
            TestContext.Current.CancellationToken);

        fault.ArrivalCount.Should().BeGreaterThan(0, "the walk fault must actually have been injected");
        frame.SnapshotIncomplete.Should().BeTrue(
            "a walk that could not list its children produced a shorter subtree, and 'no such "
            + "child' read off it is a store fault wearing a data answer");
        frame.SilentProviders.Should().Contain(((IMeshQueryProvider)provider).Name);
    }
}
