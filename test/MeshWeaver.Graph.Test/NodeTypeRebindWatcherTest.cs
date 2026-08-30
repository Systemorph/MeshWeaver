using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for the NodeType rebind watcher
/// (<see cref="NodeTypeRebindWatcher"/>) — the fix for issue #1104, where a hub activated
/// against a node with the WRONG (or no) NodeType kept the configuration it was born with for
/// its whole lifetime, because a hosted hub is pinned by address and routing never resolves its
/// path again.
///
/// <para>The contract pinned here:</para>
/// <list type="bullet">
///   <item>A post-commit Created/Updated for THIS node carrying a DIFFERENT NodeType posts a
///     self-<see cref="DisposeRequest"/> — the <c>RecycleLayoutArea</c> idiom — targeting the
///     instance hub's OWN address.</item>
///   <item>Exactly ONCE: a second qualifying event never posts a second recycle, so a flapping
///     writer cannot turn the watcher into a recycle storm.</item>
///   <item>Events for OTHER paths (a satellite, a sibling, a child) and events that carry the
///     SAME type (every ordinary content write) are ignored — those are the overwhelming
///     majority, and recycling on them would tear down live hubs for nothing.</item>
///   <item><see cref="MeshChangeKind.Deleted"/> never fires: the delete path tears the hub down
///     itself and the event carries no authoritative type.</item>
///   <item>Disposing the watcher (the hub's <c>RegisterForDisposal</c> hook) stops it — no post
///     after teardown.</item>
/// </list>
///
/// <para>🚨 Drives <see cref="NodeTypeRebindWatcher.Arm"/> against a REAL, hosted
/// <see cref="IMessageHub"/> (<see cref="HubTestBase"/>) — never a mocked one
/// (Systemorph/MeshWeaver#1810: AGENTS.md forbids mocking <c>IMessageHub</c>). The watcher's only
/// hub-shaped side effect is a self-<see cref="DisposeRequest"/>, so the assertion below is the
/// REAL effect — the hub actually disposes — rather than an intercepted call, which is a stronger
/// proof than "Post was called with the right target": a wrongly-targeted post would simply never
/// dispose THIS hub.</para>
/// </summary>
public class NodeTypeRebindWatcherTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string InstancePath = "Store";
    private const string BoundType = "Space";
    private const string RealType = "Store/Catalog";

    /// <summary>
    /// A hand-rolled feed: the production <see cref="InProcessMeshChangeFeed"/> is a HOT subject
    /// with no replay, and that property is load-bearing (a freshly-armed watcher must never see
    /// the state it was armed on — the replay-and-recycle hot loop that forced the overlay
    /// watcher's version gate). This stand-in has the same shape.
    /// </summary>
    private sealed class TestChangeFeed : IMeshChangeFeed
    {
        private readonly List<Action<MeshChangeEvent>> handlers = [];
        public void Publish(MeshChangeEvent change)
        {
            foreach (var handler in handlers.ToArray())
                handler(change);
        }
        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        {
            void Wrapped(MeshChangeEvent e)
            {
                if (filter is null || e.Kind == filter)
                    handler(e);
            }
            handlers.Add(Wrapped);
            return System.Reactive.Disposables.Disposable.Create(() => handlers.Remove(Wrapped));
        }
    }

    /// <summary>
    /// A real, freshly hosted instance hub at <see cref="InstancePath"/>, plus a REAL reactive
    /// signal for "this hub has disposed" — <see cref="IMessageHub.RegisterForDisposal(Action{IMessageHub})"/>
    /// is production infrastructure, not a mock.
    /// </summary>
    private (IMessageHub Hub, IObservable<bool> Disposed) BuildInstanceHub()
    {
        var hub = Mesh.GetHostedHub(new Address(InstancePath), c => c);
        var disposed = new ReplaySubject<bool>(1);
        hub.RegisterForDisposal(_ =>
        {
            disposed.OnNext(true);
            disposed.OnCompleted();
        });
        return (hub, disposed);
    }

    private static MeshChangeEvent Change(
        string path, string? nodeType, MeshChangeKind kind = MeshChangeKind.Updated)
        => new("", path.Split('/')[^1], path, kind, nodeType, 1, DateTimeOffset.UtcNow);

    /// <summary>Bounded wait for the real dispose signal — <c>false</c> means it never fired within the window.</summary>
    private static async Task<bool> DisposedWithinAsync(IObservable<bool> disposed, TimeSpan window)
    {
        try { return await disposed.FirstAsync().Timeout(window).Await(); }
        catch (TimeoutException) { return false; }
    }

    private static async Task AssertNoDisposeAsync(IObservable<bool> disposed) =>
        (await DisposedWithinAsync(disposed, TimeSpan.FromMilliseconds(300)))
            .Should().BeFalse("no rebind condition has fired yet");

    private static async Task AssertDisposedExactlyOnceAsync(IObservable<bool> disposed) =>
        (await DisposedWithinAsync(disposed, TimeSpan.FromSeconds(10)))
            .Should().BeTrue("Take(1): the rebind must post its self-recycle exactly once");

    [Fact]
    public async Task RetypeOfThisNode_RecyclesTheHub_ExactlyOnce()
    {
        var (hub, disposed) = BuildInstanceHub();
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        // Ordinary content writes republish the node with its type unchanged — by far the common
        // case, and recycling on them would tear down a live hub on every save.
        feed.Publish(Change(InstancePath, BoundType));
        await AssertNoDisposeAsync(disposed);

        // A write to a satellite / sibling / child is not this node.
        feed.Publish(Change($"{InstancePath}/_Access/grant", "AccessAssignment"));
        feed.Publish(Change("Store2", RealType));
        await AssertNoDisposeAsync(disposed);

        // THE signal: this node is now a different type from the one the hub bound. This is the
        // REAL recycle — the hub actually disposes, proving the DisposeRequest was posted AND
        // targeted this instance's own address (a mis-targeted post could never do this).
        feed.Publish(Change(InstancePath, RealType));
        await AssertDisposedExactlyOnceAsync(disposed);

        // Take(1): a flapping writer can never turn this into a recycle storm. The hub is already
        // disposed, so a further event reaching the watcher would be the only way to observe a
        // second attempt — Arm's Take(1) means the subscription is gone, so nothing happens.
        feed.Publish(Change(InstancePath, "Store/Plugin"));
        feed.Publish(Change(InstancePath, RealType));
    }

    /// <summary>
    /// #1104's own shape: the hub activated on a node that had NO type at all (the fabricated
    /// partition-root placeholder, or the row inside the install window), so it bound the mesh
    /// DEFAULT configuration. The arrival of the real type is the signal.
    /// </summary>
    [Fact]
    public async Task TypeArrivingOnATypelessNode_RecyclesTheHub()
    {
        var (hub, disposed) = BuildInstanceHub();
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(
            feed, hub, InstancePath, boundNodeType: null, logger: null);

        // Still type-less — nothing has changed for the binding.
        feed.Publish(Change(InstancePath, null));
        feed.Publish(Change(InstancePath, ""));
        await AssertNoDisposeAsync(disposed);

        feed.Publish(Change(InstancePath, RealType, MeshChangeKind.Created));
        await AssertDisposedExactlyOnceAsync(disposed);
    }

    /// <summary>
    /// Enrichment resolves NodeType paths case-insensitively (the static-provider lookup /
    /// <c>FindStaticNode</c>), so a case-only difference would recycle a hub onto the byte-identical
    /// configuration — churn for nothing.
    /// </summary>
    [Fact]
    public async Task CaseOnlyDifference_IsNotARebind()
    {
        var (hub, disposed) = BuildInstanceHub();
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, RealType, logger: null);

        feed.Publish(Change(InstancePath, RealType.ToUpperInvariant()));
        await AssertNoDisposeAsync(disposed);
    }

    /// <summary>
    /// A delete tears the hub down through the delete path itself, and the event carries no
    /// authoritative type — recycling on it would race that teardown for no gain.
    /// </summary>
    [Fact]
    public async Task Delete_NeverRecycles()
    {
        var (hub, disposed) = BuildInstanceHub();
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        feed.Publish(Change(InstancePath, nodeType: null, MeshChangeKind.Deleted));
        await AssertNoDisposeAsync(disposed);
    }

    /// <summary>
    /// 🚨 A null HubConfiguration must SURVIVE the wrap. Both activation sites
    /// (<c>MonolithRoutingService.CreateHub</c>, <c>MessageHubGrain</c>) branch on it to activate
    /// the fail-fast NACK-fallback hub, which answers every message with a typed DeliveryFailure
    /// naming the node type and DeactivateOnIdle's so the next access retries. Making it non-null
    /// unconditionally would kill that branch and swap fail-fast for a bare hub that Ignores typed
    /// requests — the park class, where senders wait out their whole budget instead of getting a
    /// diagnostic. Same rule <c>NodeTypeEnrichmentHelpers.ApplyStreamResult</c> states for its wrap.
    /// </summary>
    [Fact]
    public void NullHubConfiguration_IsLeftNull()
    {
        var node = new MeshNode("Catalog", "Store") { NodeType = "Store/Catalog" };
        node.HubConfiguration.Should().BeNull("precondition: the wrap's input has no configuration");

        // meshHub is captured in a closure but never dereferenced until real hub activation
        // (WithInitialization runs later) — the mesh router hub is a genuine IMessageHub, so it
        // stands in for free; no mock needed.
        var wrapped = NodeTypeRebindWatcher.WithNodeTypeRebind(node, Mesh, logger: null);

        wrapped.HubConfiguration.Should().BeNull(
            "the fail-fast NACK-fallback branch keys on a null HubConfiguration — a hub with no "
            + "configuration at all already retries on the next access, so it was never pinned");
        wrapped.Should().BeSameAs(node, "there is nothing to wrap, so the node passes through");
    }

    /// <summary>
    /// The ordinary case: a node that DOES carry a configuration keeps it (composed), so the wrap
    /// never replaces the type's own areas — it only appends the watcher's initialization.
    /// </summary>
    [Fact]
    public void NonNullHubConfiguration_IsComposedNotReplaced()
    {
        var applied = 0;
        var node = new MeshNode("Catalog", "Store")
        {
            NodeType = "Store/Catalog",
            HubConfiguration = c => { applied++; return c; }
        };

        var wrapped = NodeTypeRebindWatcher.WithNodeTypeRebind(node, Mesh, logger: null);

        wrapped.HubConfiguration.Should().NotBeNull().And.NotBeSameAs(node.HubConfiguration);
        wrapped.HubConfiguration!(new MessageHubConfiguration(
            new ServiceCollection().BuildServiceProvider(), new Address("Store")));
        applied.Should().Be(1, "the node's own configuration must still be applied");
    }

    [Fact]
    public async Task DisposingHub_StopsTheWatcher()
    {
        var (hub, disposed) = BuildInstanceHub();
        var feed = new TestChangeFeed();
        var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        // The hub's RegisterForDisposal hook: tearing the hub down disposes the watcher, so a
        // late retype must not post to a dead address.
        watcher.Dispose();
        feed.Publish(Change(InstancePath, RealType));
        await AssertNoDisposeAsync(disposed);
    }

    /// <summary>
    /// A hub already tearing down needs no recycle, and posting to it only adds a delivery its
    /// disposal has to drain.
    ///
    /// <para>🚨 With a real hub, "Post was not called" is no longer directly observable — the
    /// try/catch around the watcher's whole handler already makes a redundant Post harmless, and
    /// an idempotent <c>Dispose()</c> means the SAME dispose signal fires whether the watcher
    /// posts again or not, so it cannot discriminate the two. What stays genuinely provable, and
    /// is the property that actually matters operationally, is that firing the watcher against an
    /// already-disposed hub — the late-event race this guard exists for — never throws back into
    /// the caller: <c>IMeshChangeFeed.Publish</c> runs synchronously on the STORAGE WRITER's
    /// thread (the post-commit <c>Do</c> in <c>StorageAdapterChangeFeedExtensions</c>), so an
    /// escaping exception here would fail an unrelated caller's save.</para>
    /// </summary>
    [Fact]
    public void HubAlreadyDisposing_DoesNotThrow()
    {
        var (hub, _) = BuildInstanceHub();
        // Real, one-way teardown — there is no test-only "pretend it's disposing" flag on a real
        // hub, so drive the SAME disposal the watcher is supposed to defer to.
        hub.Dispose();
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        var thrown = Record.Exception(() => feed.Publish(Change(InstancePath, RealType)));
        thrown.Should().BeNull(
            "a late rebind event against an already-disposed hub must never propagate back into "
            + "the storage writer that published it");
    }
}
