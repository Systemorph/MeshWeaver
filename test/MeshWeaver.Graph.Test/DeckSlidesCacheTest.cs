using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="DeckSlidesCache"/>: the deck's sibling-slide query
/// must be SHARED across the slides of one deck (one live
/// <c>IMeshService.Query</c> subscription per parent path, Replay(1) semantics
/// for late subscribers) while distinct decks each get their own query.
///
/// <para>🚨 Drives the cache against a REAL <see cref="IMeshService"/> (<see cref="MonolithMeshTestBase"/>)
/// — never a mocked one (Systemorph/MeshWeaver#1810: AGENTS.md forbids mocking <c>IMeshService</c>).
/// <see cref="CountingMeshService"/> is a DECORATOR, not a mock: every call forwards to the real
/// mesh service and counts SUBSCRIPTIONS at the point <see cref="IMeshService.Query{T}"/> is
/// actually subscribed — the same observable point the original substitute counted, except the
/// data behind it is now the real mesh's real query pipeline.</para>
///
/// <para>🚨 <c>SlideNodeType.NodeType</c> ("Slide") is a RETIRED type — no builder registers it
/// any more (slides now ship as the Publish pack's dynamic <c>Publish/Slide</c>; see
/// <see cref="SlideNodeType"/>'s own doc comment), and <c>Publish/Slide</c> itself needs the whole
/// pack installed. <see cref="IMeshService.CreateNode"/> correctly refuses both with "NodeType …
/// is not registered" — a real validation this test's data must respect. So the fixture slides are
/// seeded declaratively via <c>ConfigureMesh</c>'s <see cref="MeshBuilder.AddMeshNodes"/> (raw
/// config-time storage seeding, same idiom <c>EventSubscriptionTypeRegistrationTest</c> uses),
/// which is NOT routed through the runtime <c>CreateNodeRequest</c> registration check — exactly
/// as production data for a retired-but-still-queried type would already be sitting in storage.
/// </para>
/// </summary>
public class DeckSlidesCacheTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode("s1", "DeckA") { NodeType = SlideNodeType.NodeType, Order = 1 },
                new MeshNode("s1", "DeckC") { NodeType = "Publish/Slide", Order = 2 },
                new MeshNode("s2", "DeckC") { NodeType = SlideNodeType.NodeType, Order = 1 },
                new MeshNode("notes", "DeckC") { NodeType = "Markdown" });

    private IMeshService RealMeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    /// <summary>
    /// Forwards every call to a REAL <see cref="IMeshService"/>; counts SUBSCRIPTIONS (not calls)
    /// per query string, mirroring the counting idiom the substitute-based version of this test
    /// used, but over the real query pipeline instead of a stubbed one.
    /// </summary>
    private sealed class CountingMeshService(IMeshService inner) : IMeshService
    {
        public ConcurrentDictionary<string, int> Subscriptions { get; } = new();

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request) =>
            Observable.Defer(() =>
            {
                Subscriptions.AddOrUpdate(request.Query, 1, (_, n) => n + 1);
                return inner.Query<T>(request);
            });

        public IObservable<MeshNode> CreateNode(MeshNode node) => inner.CreateNode(node);
        public IObservable<CreateNodesResponse> CreateNodes(IReadOnlyCollection<MeshNode> nodes) => inner.CreateNodes(nodes);
        public IObservable<MeshNode> UpdateNode(MeshNode node) => inner.UpdateNode(node);
        public IObservable<MeshNode> CreateOrUpdateNode(MeshNode node) => inner.CreateOrUpdateNode(node);
        public IObservable<bool> DeleteNode(string path) => inner.DeleteNode(path);
        public IObservable<MeshNode> CopyNode(string sourcePath, string targetPath,
            bool includeDescendants = true, bool includeSatellites = false) =>
            inner.CopyNode(sourcePath, targetPath, includeDescendants, includeSatellites);
        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request) => inner.Query(request);
        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
            int limit = 10, string? contextPath = null, string? context = null) =>
            inner.Autocomplete(basePath, prefix, mode, limit, contextPath, context);
        public IObservable<T?> Select<T>(string path, string property) => inner.Select<T>(path, property);
        public IObservable<string?> GetPreRenderedHtml(string path) => inner.GetPreRenderedHtml(path);
    }

    private CountingMeshService MakeCountingMesh() => new(RealMeshService);

    /// <summary>
    /// Waits for the sibling query to actually see the deck's slides — the real query pipeline is
    /// eventually consistent, unlike the old mock's synchronous <c>Observable.Return</c>.
    /// </summary>
    private Task WaitForSiblingQueryToSee(IMeshService mesh, string deck, int expectedCount) =>
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{deck}") with { UserId = WellKnownUsers.System })
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count(n => SlideNodeType.Matches(n.NodeType)) >= expectedCount)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    private DeckSlidesCache MakeCache(IMeshService mesh) =>
        new(() => mesh,
            _ => Observable.Never<MeshNode?>(),
            () => JsonOptions);

    [Fact(Timeout = 30000)]
    public async Task GetOrderedSlides_ConcurrentSubscribers_ShareOneQuerySubscription()
    {
        await WaitForSiblingQueryToSee(RealMeshService, "DeckA", 1);

        var mesh = MakeCountingMesh();
        var cache = MakeCache(mesh);

        var firstTask = cache.GetOrderedSlides("DeckA").FirstAsync().Timeout(30.Seconds()).ToTask();
        var secondTask = cache.GetOrderedSlides("DeckA").FirstAsync().Timeout(30.Seconds()).ToTask();
        var results = await Task.WhenAll(firstTask, secondTask);

        mesh.Subscriptions.Values.Sum().Should().Be(1,
            "concurrent subscribers for the same deck must share ONE underlying sibling query");
        results[0].Should().HaveCount(1);
        results[0][0].Path.Should().Be("DeckA/s1");
        results[1].Should().HaveCount(1);
        results[1][0].Path.Should().Be("DeckA/s1");
    }

    /// <summary>
    /// After the LAST subscriber disconnects, the shared per-deck pipeline must
    /// disconnect fully — a plain <c>Replay(1).RefCount()</c> with NO time-delayed
    /// hold. A re-subscribe therefore re-runs the query. This is the leak guard at
    /// the unit level: a <c>RefCount(TimeSpan)</c> would keep the source connected
    /// via a process-global <see cref="System.Threading.Timer"/> that roots the
    /// pipeline closure → mesh hub past mesh disposal (repro:
    /// <c>MeshHubDisposalLeakTest</c>). Overlapping subscribers still share one
    /// query (the test above); only a genuine subscriber-free gap disconnects.
    ///
    /// <para>Subscription counting happens at SUBSCRIBE time (RefCount's connect), not at data
    /// arrival, so this holds regardless of how long the real query takes to actually emit —
    /// subscribe-then-immediately-dispose is exactly the scenario a mock could not distinguish
    /// from "the query never ran" without also proving the connect happened synchronously.</para>
    /// </summary>
    [Fact]
    public void GetOrderedSlides_AfterLastUnsubscribe_DisconnectsWithNoLingeringTimer()
    {
        var mesh = MakeCountingMesh();
        var cache = MakeCache(mesh);

        cache.GetOrderedSlides("DeckA").Subscribe().Dispose();
        mesh.Subscriptions.Values.Sum().Should().Be(1, "the first subscription ran the query once");

        // Re-subscribe after the refcount already hit zero: with a plain RefCount the
        // source is fully disconnected, so this re-runs the query (count → 2). A
        // lingering disconnect-delay timer would instead replay the warm buffer (count
        // stuck at 1) — AND root the hub in the global TimerQueue.
        cache.GetOrderedSlides("DeckA").Subscribe().Dispose();
        mesh.Subscriptions.Values.Sum().Should().Be(2,
            "with no disconnect-delay timer, a re-subscribe after the refcount hit zero " +
            "re-runs the query rather than replaying from a timer-held connection");
    }

    [Fact]
    public void GetOrderedSlides_DifferentParents_GetSeparateQueries()
    {
        var mesh = MakeCountingMesh();
        var cache = MakeCache(mesh);

        using var a1 = cache.GetOrderedSlides("DeckA").Subscribe();
        using var a2 = cache.GetOrderedSlides("DeckA").Subscribe();
        using var b = cache.GetOrderedSlides("DeckB").Subscribe();

        mesh.Subscriptions.Should().HaveCount(2,
            "each parent path owns exactly one sibling query");
        mesh.Subscriptions.Keys.Should().Contain(k => k.Contains("namespace:DeckA"));
        mesh.Subscriptions.Keys.Should().Contain(k => k.Contains("namespace:DeckB"));
        mesh.Subscriptions.Values.Should().OnlyContain(count => count == 1);
    }

    /// <summary>
    /// The sibling pipeline is suffix-aware end to end: the query carries NO
    /// <c>nodeType:</c> term (that filter is EQUALITY, so it silently excluded every
    /// plugin-typed slide — education's <c>Publish/Slide</c> nodes rendered
    /// "Slide 1 / 1" with no Prev/Next), the fold keeps built-in AND <c>*/Slide</c>
    /// types while dropping non-slide children, and a plugin-typed parent
    /// (<c>*/Deck</c>) still gets its manifest order applied.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task BuildOrderedSlides_PluginTypedSlidesAndDeck_AreSuffixAware()
    {
        await WaitForSiblingQueryToSee(RealMeshService, "DeckC", 2);

        var mesh = MakeCountingMesh();
        var parent = new MeshNode("DeckC", "")
        {
            NodeType = "Publish/Deck",
            Content = new DeckContent { Slides = ["s1", "s2"] }
        };

        // The manifest stream is internally seeded with StartWith(null) (see BuildOrderedSlides),
        // so the FIRST combined emission orders by MeshNode.Order (the fallback) before the
        // parent's manifest has been consulted; only a LATER emission reflects the manifest order.
        // The pipeline never completes (a live sibling query), so wait for the settled shape
        // rather than taking a fixed index.
        var settled = await DeckSlidesCache.BuildOrderedSlides(
                mesh,
                Observable.Return<MeshNode?>(parent),
                "DeckC",
                JsonOptions,
                accessService: null)
            .Where(list => list.Select(n => n.Path).SequenceEqual(["DeckC/s1", "DeckC/s2"]))
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        mesh.Subscriptions.Keys.Should().ContainSingle().Which.Should().Be("namespace:DeckC",
            "the sibling query must not carry an equality nodeType filter");
        settled.Select(n => n.Path).Should().Equal("DeckC/s1", "DeckC/s2");
    }
}
