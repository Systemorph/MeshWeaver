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
/// Pins the catalog-search re-render storm (atioz, 2026-06-22). The new top-bar AI menu opens
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

        using var subscription = Query.Query<MeshNode>(MeshQueryRequest.FromQuery(catalogQuery))
            .Subscribe(change =>
            {
                if (change.ChangeType == QueryChangeType.Updated
                    && change.Items.Any(n => string.Equals(n.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
                    Interlocked.Increment(ref updatedForTarget);

                if (accumulator.Apply(change))
                    Interlocked.Increment(ref rerenders);
            });

        // Let the initial set settle (Initial, plus any trailing Added if the index lagged a create).
        await WaitUntil(() => accumulator.Nodes.Count >= 3, ReadNodeTimeout);
        var rerendersAfterLoad = Volatile.Read(ref rerenders);
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
}
