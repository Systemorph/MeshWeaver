using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the catalog-search re-render storm (prod, 2026-06-22). The new top-bar AI menu opens
/// <c>/search?q=nodeType:Thread&amp;groupBy=Namespace</c> (and Models / Agents / Skills) — an
/// UNSCOPED query with no <c>namespace:</c> filter, so the search view subscribes to EVERY
/// partition's live change feed. The legacy <c>MeshSearchView.LoadResults</c> re-ran the full
/// grouping/sort + <c>StateHasChanged</c> on EVERY emission, including content-only
/// <see cref="QueryChangeType.Updated"/> changes — so a busy mesh (every thread/agent/model content
/// write across all partitions) turned the catalog page into a re-render firehose that slowed the
/// whole app.
///
/// The fix folds the live stream through <see cref="SearchResultAccumulator"/>, which re-renders
/// ONLY on structural set changes (add / remove / reset) and treats a content-only Updated as a
/// no-op (result cards databind their own content via LayoutAreaView). These tests pin that
/// contract: a deterministic, bounded re-render count under a content-update storm.
/// </summary>
public class SearchResultAccumulatorTest
{
    private static MeshNode Node(string id) =>
        new(id, "storm-ns") { Name = id, NodeType = "Markdown" };

    private static QueryResultChange<MeshNode> Change(QueryChangeType type, params MeshNode[] items) =>
        new() { ChangeType = type, Items = items };

    [Fact]
    public void ContentUpdateStorm_TriggersNoRerender_OnlyStructuralChangesDo()
    {
        var accumulator = new SearchResultAccumulator();

        // Initial set of three — the one legitimate first-frame render.
        accumulator.Apply(Change(QueryChangeType.Initial, Node("a"), Node("b"), Node("c")))
            .Should().BeTrue("the Initial frame is a structural render");
        accumulator.Nodes.Should().HaveCount(3);

        // A storm of 1000 content-only updates (the firehose) — NONE may re-render.
        var rerenders = 0;
        for (var i = 0; i < 1000; i++)
            if (accumulator.Apply(Change(QueryChangeType.Updated, Node("a"), Node("b"))))
                rerenders++;
        rerenders.Should().Be(0,
            "content-only Updated emissions from the unscoped catalog feed must never re-render the grid");

        // Structural changes DO re-render — and only the genuinely-structural ones.
        accumulator.Apply(Change(QueryChangeType.Added, Node("d")))
            .Should().BeTrue("adding a new path is structural");
        accumulator.Nodes.Should().HaveCount(4);

        accumulator.Apply(Change(QueryChangeType.Added, Node("a")))
            .Should().BeFalse("re-adding an already-present path is not structural");

        accumulator.Apply(Change(QueryChangeType.Removed, Node("b")))
            .Should().BeTrue("removing a present path is structural");
        accumulator.Nodes.Should().HaveCount(3);

        accumulator.Apply(Change(QueryChangeType.Removed, Node("zzz")))
            .Should().BeFalse("removing an absent path is not structural");

        accumulator.Apply(Change(QueryChangeType.Reset, Node("x")))
            .Should().BeTrue("a reset re-renders");
        accumulator.Nodes.Should().ContainSingle().Which.Path.Should().Be("storm-ns/x");
    }
}

/// <summary>
/// End-to-end companion to <see cref="SearchResultAccumulatorTest"/>: drives the REAL mesh change
/// feed and proves (a) the firehose is real — a content-update storm yields one
/// <see cref="QueryChangeType.Updated"/> emission per write — yet (b) the search-view consumer
/// (the <see cref="SearchResultAccumulator"/> the component folds through) re-renders ZERO extra
/// times for the whole storm.
/// </summary>
public class SearchResultStormTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService Query => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Id of the marker node created after the catalog's real leaves. Its arrival is the ordered
    /// change feed's proof that the initial load — including any trailing <c>Added</c> frames from
    /// an index that lagged a create — has been fully applied and counted.
    /// </summary>
    private const string QuiesceId = "zz-quiesce";

    /// <summary>
    /// Id of the satellite node written under the catalog root by
    /// <see cref="CatalogSubscription_SatelliteWriteUnderTheRoot_NeverEntersTheResultSet"/>.
    /// </summary>
    private const string SatelliteId = "leaked-satellite";

    private static Task WaitUntil(Func<bool> condition, TimeSpan timeout) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => condition())
            .Where(ok => ok)
            .FirstAsync()
            .Timeout(timeout)
            .ToTask();

    [Fact(Timeout = 60000)]
    public async Task CatalogSubscription_ContentUpdateStorm_AddsNoExtraRerenders()
    {
        var root = $"{TestPartition}/storm";
        var catalogQuery = $"path:{root} scope:descendants";
        var targetPath = $"{root}/a";
        var quiescePath = $"{root}/{QuiesceId}";

        // A small live catalog: three leaves the search view would render as cards.
        await NodeFactory.CreateNode(
            new MeshNode("storm", TestPartition) { Name = "Storm", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("a", root) { Name = "A v0", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("b", root) { Name = "B", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("c", root) { Name = "C", NodeType = "Markdown" }).Should().Emit();

        // The search view's exact decision logic: fold every change through the accumulator and
        // count a re-render only when Apply() reports a structural change.
        var accumulator = new SearchResultAccumulator();
        var rerenders = 0;
        var updatedForTarget = 0;
        // -1 until the load is provably finished; latched inside the subscription. See the 🚨 below.
        var rerendersAtQuiescence = -1;

        using var subscription = Query.Query<MeshNode>(MeshQueryRequest.FromQuery(catalogQuery))
            .Subscribe(change =>
            {
                if (change.ChangeType == QueryChangeType.Updated
                    && change.Items.Any(n => string.Equals(n.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
                    Interlocked.Increment(ref updatedForTarget);

                if (accumulator.Apply(change))
                    Interlocked.Increment(ref rerenders);

                // Latched AFTER the increment, so the baseline can never miss the render that
                // completed the load, and inside the callback, so no reader sees the pair
                // half-updated.
                if (rerendersAtQuiescence < 0
                    && accumulator.Nodes.Any(n => string.Equals(n.Path, quiescePath, StringComparison.OrdinalIgnoreCase)))
                    Volatile.Write(ref rerendersAtQuiescence, Volatile.Read(ref rerenders));
            });

        // 🚨 The baseline needs a POSITIVE signal that structural churn has finished, and a count
        // threshold is not one. Waiting for `Nodes.Count >= 3` releases the moment the third node
        // lands, but the load is not over then: the index can lag a create, so trailing `Added`
        // frames for the first three keep arriving and each is a structural render. The old
        // baseline was therefore whatever the count happened to be at an arbitrary point inside
        // that churn, and the final exact-equality assertion blamed the storm for renders that had
        // already happened — reproduced locally as "Expected 1, but found 3", and it reds
        // unrelated PRs (seen on #1176, shard 1). It is a defect in the test, not in the code
        // under test.
        //
        // A marker node created AFTER the first three turns ordering into that signal: the change
        // feed is ordered, so once the marker has been applied, every structural frame for a/b/c
        // is already applied AND already counted. No sleep, no tolerance, no weakened assertion —
        // the storm must still add exactly zero.
        //
        // 🚨 The marker must be created only once the INITIAL frame has actually arrived. The
        // subscription above does not establish itself synchronously — `IMeshService.Query<T>` runs
        // its subscribe + initial snapshot through the IO pool — so creating the marker straight
        // after `.Subscribe(...)` races that: the create can land BEFORE the snapshot is taken, in
        // which case the marker arrives INSIDE the Initial payload rather than as an ordered
        // post-snapshot change. The latch would then fire on the very first frame, baselining
        // before the trailing `Added` frames it exists to wait for — and those later renders would
        // be blamed on the storm, which is precisely the failure this test was written to stop.
        // Waiting for the first structural render first makes the marker demonstrably post-Initial,
        // which is the whole basis of the ordering argument above.
        await WaitUntil(() => Volatile.Read(ref rerenders) >= 1, ReadNodeTimeout);
        await NodeFactory.CreateNode(
            new MeshNode(QuiesceId, root) { Name = "quiesce marker", NodeType = "Markdown" }).Should().Emit();
        await WaitUntil(() => Volatile.Read(ref rerendersAtQuiescence) >= 0, ReadNodeTimeout);
        var rerendersAfterLoad = Volatile.Read(ref rerendersAtQuiescence);
        rerendersAfterLoad.Should().BeGreaterThanOrEqualTo(1, "the first frame is a structural render");

        // Fire a storm of content updates to ONE node — exactly what a busy mesh does to a thread.
        const int updateCount = 25;
        for (var i = 1; i <= updateCount; i++)
            await NodeFactory.UpdateNode(
                new MeshNode("a", root) { Name = $"A v{i}", NodeType = "Markdown" }).Should().Emit();

        // The live change feed DID deliver the whole storm — the firehose is real.
        await WaitUntil(() => Volatile.Read(ref updatedForTarget) >= updateCount, ReadNodeTimeout);

        // ...but the search view re-rendered ZERO extra times for it. Before the fix, the legacy
        // LoadResults re-rendered (re-grouped + StateHasChanged) on every one of these.
        Volatile.Read(ref rerenders).Should().Be(rerendersAfterLoad,
            "content-only Updated emissions must add no re-renders to the catalog grid (the storm fix)");
        Volatile.Read(ref updatedForTarget).Should().BeGreaterThanOrEqualTo(updateCount,
            "the change feed delivered every content update — the storm is real; the fix is in the consumer");
    }

    /// <summary>
    /// Pins the defect that made the storm test above a coin flip: the live catalog feed must apply
    /// the SAME result-set exclusions its snapshot does.
    ///
    /// <para>A content query never returns satellite nodes — they live in their own storage tables
    /// (<c>_Activity</c>→activities, <c>_Access</c>→access, …) and the initial snapshot filters them
    /// out. The live path did not: its write-lag supplement admitted any notified entity that passed
    /// <c>QueryEvaluator.Matches</c>, and for a bare <c>path:X scope:descendants</c> listing (no
    /// filter, no free-text term) <c>Matches</c> is true for EVERYTHING. So a satellite write under
    /// the root arrived as a structural <c>Added</c> and the next re-query took it straight back out
    /// as <c>Removed</c> — a phantom row that flashes into a live search grid, and two re-renders of
    /// the whole result list that nothing in the result set justifies.</para>
    ///
    /// <para>#1193 taught that supplement the snapshot's exclusions; #1250 deleted it outright once
    /// the same shape turned out to bypass row-level security as well (<c>Matches</c> performs no
    /// permission check) and to cover no real write lag. The invariant this test pins is unchanged —
    /// it is now held by construction, because the re-query is the live path's only source of rows.</para>
    ///
    /// <para>The storm test hit it because a <c>MonotonicWriteGuard</c> resolution writes a
    /// <c>{path}/_Activity/write-conflict-{n}</c> satellite, and whether the 25-update storm produced
    /// a conflict at all was timing. This test does not race anything: it WRITES the satellite
    /// directly through storage — the exact change notification the live pipeline consumes — and uses
    /// an ordinary node created afterwards as the ordered proof that the feed processed it.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CatalogSubscription_SatelliteWriteUnderTheRoot_NeverEntersTheResultSet()
    {
        var root = $"{TestPartition}/leak";
        var catalogQuery = $"path:{root} scope:descendants";
        var mainPath = $"{root}/main";
        var satellitePath = $"{mainPath}/_Activity/{SatelliteId}";
        var markerPath = $"{root}/{QuiesceId}";

        await NodeFactory.CreateNode(
            new MeshNode("leak", TestPartition) { Name = "Leak", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("main", root) { Name = "Main", NodeType = "Markdown" }).Should().Emit();

        var accumulator = new SearchResultAccumulator();
        var structuralFrames = 0;
        var satelliteFrames = 0;
        var markerApplied = 0;

        using var subscription = Query.Query<MeshNode>(MeshQueryRequest.FromQuery(catalogQuery))
            .Subscribe(change =>
            {
                // Counted on the RAW frame, not on the accumulator: an Added the accumulator folds in
                // and a Removed it folds back out are both this defect, and both re-render the grid.
                if (change.Items.Any(n => string.Equals(n.Path, satellitePath, StringComparison.OrdinalIgnoreCase)))
                    Interlocked.Increment(ref satelliteFrames);

                if (accumulator.Apply(change))
                    Interlocked.Increment(ref structuralFrames);

                if (accumulator.Nodes.Any(n => string.Equals(n.Path, markerPath, StringComparison.OrdinalIgnoreCase)))
                    Volatile.Write(ref markerApplied, 1);
            });

        // Same ordering discipline as the storm test: act only once the Initial frame has provably
        // arrived, so everything after it is an ordered post-snapshot change.
        await WaitUntil(() => Volatile.Read(ref structuralFrames) >= 1, ReadNodeTimeout);

        // The satellite write, straight through storage — the same notification a guard-written
        // write-conflict log, a comment, or an access grant produces. CreateNodeRequest deliberately
        // refuses satellite paths (they are per-node lifecycle), so storage is the honest way to
        // deliver exactly the input the live query pipeline sees.
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await storage.Write(
            new MeshNode(SatelliteId, $"{mainPath}/_Activity")
            {
                Name = "satellite that must never be a search result",
                NodeType = "ActivityLog",
                MainNode = mainPath,
                State = MeshNodeState.Active,
                Version = 1,
            },
            Mesh.JsonSerializerOptions).Should().Emit();

        // An ORDINARY node created after it. The change feed is ordered, so once the marker has been
        // applied every frame the satellite write could have produced is already delivered and
        // counted — no sleep, no tolerance.
        await NodeFactory.CreateNode(
            new MeshNode(QuiesceId, root) { Name = "quiesce marker", NodeType = "Markdown" }).Should().Emit();
        await WaitUntil(() => Volatile.Read(ref markerApplied) == 1, ReadNodeTimeout);

        Volatile.Read(ref satelliteFrames).Should().Be(0,
            "a satellite node is not a content-query result — the snapshot excludes it, so the live "
            + "feed must never emit it either, as an Added or as the Removed that takes it back out");
        accumulator.Nodes.Should().NotContain(
            n => string.Equals(n.Path, satellitePath, StringComparison.OrdinalIgnoreCase),
            "the satellite must never reach the rendered result set");
    }
}
