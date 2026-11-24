using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// An open query over declared (static) rows follows a live module swap. The rows a module's providers
/// contribute are rebuilt from the new generation when it is swapped in, but a query used to emit its
/// Initial and say nothing more — so every live consumer (a synced query's replay cache, a resolver's
/// snapshot, the aggregator's last result) kept the rows, and the content objects they carry, of the
/// generation that had just retired. That roots the retired generation's collectible load context:
/// MeshWeaver.AI swapped live as the primary of its swap was unloaded and never collected.
/// </summary>
public class StaticNodeQueryFollowsAModuleSwapTest
{
    private const string Query = "namespace:t nodeType:Thing";

    /// <summary>Hands out a FRESH node on every catalog build — what a module's provider does when its
    /// content is built from the generation that is now current.</summary>
    private sealed class RebuildingProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes() => [Row("a"), Row("b")];
    }

    /// <summary>Hands out the SAME rows on every build — a module whose content a swap did not touch.</summary>
    private sealed class StableProvider : IStaticNodeProvider
    {
        private readonly MeshNode[] rows = [Row("a"), Row("b")];
        public IEnumerable<MeshNode> GetStaticNodes() => rows;
    }

    private static MeshNode Row(string id) => new(id, "t") { NodeType = "Thing", Name = id, Content = new object() };

    private static (List<QueryResultChange<MeshNode>> Frames, IDisposable Subscription) Open(
        StaticNodeQueryProvider provider)
    {
        var frames = new List<QueryResultChange<MeshNode>>();
        var subscription = provider
            .Query<MeshNode>(new MeshQueryRequest { Query = Query }, new JsonSerializerOptions())
            .Subscribe(frames.Add);
        return (frames, subscription);
    }

    private static void SwapAModule(ModuleContexts registry) => registry.CommitAll(new ModuleSwapStage());

    [Fact]
    public void AnOpenQuery_ReceivesTheRebuiltRows_WhenAModuleIsSwapped()
    {
        using var registry = new ModuleContexts();
        var provider = new StaticNodeQueryProvider([new RebuildingProvider()], _ => true, null, null, registry, null, null);
        var (frames, subscription) = Open(provider);
        using var _ = subscription;

        var initial = frames.Should().ContainSingle().Subject;
        initial.ChangeType.Should().Be(QueryChangeType.Initial);
        initial.Items.Select(n => n.Path).OrderBy(x => x).Should().Equal("t/a", "t/b");

        SwapAModule(registry);

        frames.Should().HaveCount(2, "the swap re-reads the query and tells the reader what changed");
        var updated = frames[1];
        updated.ChangeType.Should().Be(QueryChangeType.Updated);
        updated.Items.Select(n => n.Path).OrderBy(x => x).Should().Equal("t/a", "t/b");
        foreach (var row in updated.Items)
            initial.Items.Should().NotContain(old => ReferenceEquals(old, row),
                "the reader's copy is REPLACED by the row built from the generation now serving — "
                + "holding the old one roots the retired generation");
    }

    /// <summary>NEGATIVE CONTROL: a swap that rebuilt nothing for this reader says nothing — the follow does
    /// not turn every module swap into churn for every open declared-row query.</summary>
    [Fact]
    public void AnOpenQuery_HearsNothing_WhenTheSwapRebuiltNoneOfItsRows()
    {
        using var registry = new ModuleContexts();
        var provider = new StaticNodeQueryProvider([new StableProvider()], _ => true, null, null, registry, null, null);
        var (frames, subscription) = Open(provider);
        using var _ = subscription;
        frames.Should().ContainSingle();

        SwapAModule(registry);
        SwapAModule(registry);

        frames.Should().ContainSingle("the same rows are not announced again");
    }

    /// <summary>The registry's disposal completes <c>VersionChanged</c> to release subscribers that did not end
    /// their own subscription: an open query ends with it (and drops the rows it held) rather than outliving it.</summary>
    [Fact]
    public void AnOpenQuery_Completes_WhenTheModuleRegistryIsDisposed()
    {
        var registry = new ModuleContexts();
        var provider = new StaticNodeQueryProvider([new RebuildingProvider()], _ => true, null, null, registry, null, null);
        var completed = false;
        using var subscription = provider
            .Query<MeshNode>(new MeshQueryRequest { Query = Query }, new JsonSerializerOptions())
            .Subscribe(_ => { }, () => completed = true);
        completed.Should().BeFalse("an open query stays open while the registry lives");

        registry.Dispose();

        completed.Should().BeTrue("the registry's completion of VersionChanged is forwarded, not swallowed");
    }

    [Fact]
    public void AnEndedQuery_HearsNothing_OfALaterSwap()
    {
        using var registry = new ModuleContexts();
        var provider = new StaticNodeQueryProvider([new RebuildingProvider()], _ => true, null, null, registry, null, null);
        var (frames, subscription) = Open(provider);
        subscription.Dispose();

        SwapAModule(registry);

        frames.Should().ContainSingle("a disposed subscription no longer follows the registry");
    }
}
