using System;
using System.Collections.Generic;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using NSubstitute;
using NSubstitute.Extensions;
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
/// </summary>
public class NodeTypeRebindWatcherTest
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

    private static IMessageHub BuildInstanceHub(out Func<Func<PostOptions, PostOptions>?> capturedOptions)
    {
        var hub = Substitute.For<IMessageHub>();
        hub.Address.Returns(new Address(InstancePath));
        hub.IsDisposing.Returns(false);
        Func<PostOptions, PostOptions>? captured = null;
        // Configure() records the Post spec WITHOUT running NSubstitute's auto-value provider —
        // IMessageDelivery carries an INTERNAL member, so auto-substituting Post's return type
        // throws TypeLoadException at proxy generation (same reason as OverlaySelfHealWatcherTest).
        hub.Configure()
            .Post(Arg.Any<DisposeRequest>(),
                Arg.Do<Func<PostOptions, PostOptions>>(f => captured = f))
            .Returns((IMessageDelivery<DisposeRequest>?)null);
        capturedOptions = () => captured;
        return hub;
    }

    private static MeshChangeEvent Change(
        string path, string? nodeType, MeshChangeKind kind = MeshChangeKind.Updated)
        => new("", path.Split('/')[^1], path, kind, nodeType, 1, DateTimeOffset.UtcNow);

    private static void AssertNoDispose(IMessageHub hub) =>
        hub.DidNotReceive().Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    private static void AssertDisposedExactlyOnce(IMessageHub hub) =>
        hub.Received(1).Post(
            Arg.Any<DisposeRequest>(), Arg.Any<Func<PostOptions, PostOptions>>());

    [Fact]
    public void RetypeOfThisNode_RecyclesTheHub_ExactlyOnce()
    {
        var hub = BuildInstanceHub(out var capturedOptions);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        // Ordinary content writes republish the node with its type unchanged — by far the common
        // case, and recycling on them would tear down a live hub on every save.
        feed.Publish(Change(InstancePath, BoundType));
        AssertNoDispose(hub);

        // A write to a satellite / sibling / child is not this node.
        feed.Publish(Change($"{InstancePath}/_Access/grant", "AccessAssignment"));
        feed.Publish(Change("Store2", RealType));
        AssertNoDispose(hub);

        // THE signal: this node is now a different type from the one the hub bound.
        feed.Publish(Change(InstancePath, RealType));
        AssertDisposedExactlyOnce(hub);

        // The post targets the instance's OWN address (the RecycleLayoutArea idiom). Derive the
        // expectation from the SAME base options so the auto-generated MessageId does not perturb
        // record equality.
        var options = capturedOptions();
        options.Should().NotBeNull("the DisposeRequest must be posted with explicit options");
        var baseOptions = new PostOptions(new Address("sender"));
        options!(baseOptions).Should().Be(baseOptions.WithTarget(new Address(InstancePath)),
            "the rebind recycle must target the instance hub's own address");

        // Take(1): a flapping writer can never turn this into a recycle storm.
        feed.Publish(Change(InstancePath, "Store/Plugin"));
        feed.Publish(Change(InstancePath, RealType));
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// #1104's own shape: the hub activated on a node that had NO type at all (the fabricated
    /// partition-root placeholder, or the row inside the install window), so it bound the mesh
    /// DEFAULT configuration. The arrival of the real type is the signal.
    /// </summary>
    [Fact]
    public void TypeArrivingOnATypelessNode_RecyclesTheHub()
    {
        var hub = BuildInstanceHub(out _);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(
            feed, hub, InstancePath, boundNodeType: null, logger: null);

        // Still type-less — nothing has changed for the binding.
        feed.Publish(Change(InstancePath, null));
        feed.Publish(Change(InstancePath, ""));
        AssertNoDispose(hub);

        feed.Publish(Change(InstancePath, RealType, MeshChangeKind.Created));
        AssertDisposedExactlyOnce(hub);
    }

    /// <summary>
    /// Enrichment resolves NodeType paths case-insensitively (the static-provider lookup /
    /// <c>FindStaticNode</c>), so a case-only difference would recycle a hub onto the byte-identical
    /// configuration — churn for nothing.
    /// </summary>
    [Fact]
    public void CaseOnlyDifference_IsNotARebind()
    {
        var hub = BuildInstanceHub(out _);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, RealType, logger: null);

        feed.Publish(Change(InstancePath, RealType.ToUpperInvariant()));
        AssertNoDispose(hub);
    }

    /// <summary>
    /// A delete tears the hub down through the delete path itself, and the event carries no
    /// authoritative type — recycling on it would race that teardown for no gain.
    /// </summary>
    [Fact]
    public void Delete_NeverRecycles()
    {
        var hub = BuildInstanceHub(out _);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        feed.Publish(Change(InstancePath, nodeType: null, MeshChangeKind.Deleted));
        AssertNoDispose(hub);
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
        var meshHub = Substitute.For<IMessageHub>();
        var node = new MeshNode("Catalog", "Store") { NodeType = "Store/Catalog" };
        node.HubConfiguration.Should().BeNull("precondition: the wrap's input has no configuration");

        var wrapped = NodeTypeRebindWatcher.WithNodeTypeRebind(node, meshHub, logger: null);

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
        var meshHub = Substitute.For<IMessageHub>();
        var applied = 0;
        var node = new MeshNode("Catalog", "Store")
        {
            NodeType = "Store/Catalog",
            HubConfiguration = c => { applied++; return c; }
        };

        var wrapped = NodeTypeRebindWatcher.WithNodeTypeRebind(node, meshHub, logger: null);

        wrapped.HubConfiguration.Should().NotBeNull().And.NotBeSameAs(node.HubConfiguration);
        wrapped.HubConfiguration!(new MessageHubConfiguration(
            Substitute.For<IServiceProvider>(), new Address("Store")));
        applied.Should().Be(1, "the node's own configuration must still be applied");
    }

    [Fact]
    public void DisposingHub_StopsTheWatcher()
    {
        var hub = BuildInstanceHub(out _);
        var feed = new TestChangeFeed();
        var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        // The hub's RegisterForDisposal hook: tearing the hub down disposes the watcher, so a
        // late retype must not post to a dead address.
        watcher.Dispose();
        feed.Publish(Change(InstancePath, RealType));
        AssertNoDispose(hub);
    }

    /// <summary>
    /// A hub already tearing down needs no recycle, and posting to it only adds a delivery its
    /// disposal has to drain.
    /// </summary>
    [Fact]
    public void HubAlreadyDisposing_IsNotPosted()
    {
        var hub = BuildInstanceHub(out _);
        hub.IsDisposing.Returns(true);
        var feed = new TestChangeFeed();
        using var watcher = NodeTypeRebindWatcher.Arm(feed, hub, InstancePath, BoundType, logger: null);

        feed.Publish(Change(InstancePath, RealType));
        AssertNoDispose(hub);
    }
}
