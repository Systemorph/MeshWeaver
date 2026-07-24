using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Regression tests for the activity-tracking WEDGE (memex-cloud 2026-07, MCP reconnect
/// rejected) and its fix in <see cref="ActivityTrackingHub"/> +
/// <see cref="MeshNodeExtensions"/> (HandleTrackActivity).
///
/// <para><b>Root cause.</b> <see cref="TrackActivityRequest"/> used to be handled on
/// whichever hub posted it — the portal hub, or a transient per-connection / MCP
/// back-connection hub. Its <c>GetMeshNodeStream(activityPath).Update(...)</c> therefore
/// opened its <see cref="IMeshNodeStreamCache"/> sync subscription on THAT hub's cache
/// (<c>cache/{connectionId}</c>), whose initial-state / <c>PatchDataResponse</c> had to
/// route back through the hub's transient, UNREGISTERED mesh root
/// (<c>mesh/{connectionId}</c>) → <c>[ROUTE] NotFound</c> → the write stalled 30 s ("no
/// initial state arrived within 30s") → the reconnecting client was rejected.</para>
///
/// <para><b>Fix.</b> All activity tracking now originates from the dedicated, stable,
/// registered <see cref="ActivityTrackingHub"/> — hosted off the mesh ROOT, resolving the
/// mesh root's SHARED, registered <see cref="IMeshNodeStreamCache"/>
/// (<c>cache/{meshRootId}</c>) instead of a per-connection cache. Its sync subscription's
/// responses therefore route to a registered hub, never a transient one.</para>
///
/// <para>🚨 The exact 30 s stall is a DISTRIBUTED phenomenon — it needs an unregistered
/// per-connection mesh root, which a single-process monolith cannot express (in-process
/// routing resolves every hosted hub via <c>GetHostedHub</c> regardless of stream
/// registration). These tests therefore PIN THE FIX'S CONTRACT — the invariant the old
/// code violated: (1) the tracking hub is stable, hosted off the mesh root, and shares the
/// mesh-root cache (NOT a per-caller cache); (2) a track initiated from a distinct
/// connection-style hub still lands the node, because the write originates from the shared
/// tracking hub. A regression to "the write uses the calling hub's cache" fails test (1).</para>
/// </summary>
public class ActivityTrackingHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The tracking hub is a single, stable, registered hub hosted off the mesh ROOT and
    /// resolving the mesh root's SHARED cache. This is the whole point of the fix: the
    /// activity write must go through <c>cache/{meshRootId}</c> (registered, stream-routed),
    /// never a transient per-connection cache whose responses route through an unregistered
    /// <c>mesh/{connectionId}</c>.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void ActivityTrackingHub_IsStable_OffMeshRoot_AndSharesRootCache()
    {
        var trackingHub = Mesh.GetActivityTrackingHub();
        trackingHub.Should().NotBeNull();

        // Stable + idempotent — exactly one tracking hub per mesh root.
        Mesh.GetActivityTrackingHub().Should().BeSameAs(trackingHub,
            "GetActivityTrackingHub must be idempotent (a single stable hub, not per-call)");

        // Hosted off the mesh ROOT (its mesh hub is the root), at the dedicated activity address.
        trackingHub.GetMeshHub().Should().BeSameAs(Mesh,
            "the tracking hub must be hosted off the mesh root");
        trackingHub.Address.Type.Should().Be(AddressExtensions.ActivityType);

        // 🚨 Shares the mesh-root cache — resolves the SAME singleton, not its own. If a
        // future change gives the tracking hub its own cache, its writes' responses would
        // again route through its own (potentially transient) mesh root — the exact wedge.
        var rootCache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var trackingCache = trackingHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        trackingCache.Should().BeSameAs(rootCache,
            "the tracking hub must resolve the mesh root's shared, registered cache (cache/{meshRootId}), " +
            "never a per-hub cache — that shared cache is what makes the sync-subscription response routable");
    }

    /// <summary>
    /// A track INITIATED FROM A CONNECTION-STYLE HUB (the prod shape — the portal / MCP
    /// back-connection posts it) must still land the UserActivity node, because the handler
    /// originates the write from the dedicated tracking hub (shared root cache) rather than
    /// the calling hub. Pre-fix the write ran on the caller's context; post-fix it is
    /// decoupled onto the stable tracking hub.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TrackActivity_InitiatedFromConnectionHub_LandsNode()
    {
        const string user = "dave";
        const string nodePath = "dave/MyDoc";

        // ONBOARD-FIRST gate: activity tracking never creates a partition ahead of onboarding.
        await OnboardPartitionRoot(user);

        // A distinct connection-style hosted hub — the shape that used to handle the request
        // and wedge. It carries the TrackActivityRequest handler (WithGraphTypes) so a
        // self-post is handled HERE; the fix must still land the node via the tracking hub.
        var connHub = Mesh.GetHostedHub(
            new Address("client", Guid.NewGuid().ToString("N")[..12]),
            c => c.AddData().WithGraphTypes());
        connHub.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = user, Name = user });

        // Sanity: the calling hub is NOT the tracking hub, yet both resolve the same shared
        // cache — so wherever the request is handled, the write goes through cache/{meshRootId}.
        connHub.Address.Should().NotBe(connHub.GetActivityTrackingHub().Address);

        connHub.Post(new TrackActivityRequest(
            NodePath: nodePath,
            UserId: user,
            NodeName: "My Doc",
            NodeType: "Markdown",
            Namespace: user));

        var node = await PollForFirst($"namespace:{user}/_UserActivity nodeType:UserActivity");

        node.Should().NotBeNull(
            "a track posted to a connection-style hub must still land a UserActivity node — " +
            "the handler originates the write from the dedicated tracking hub, not the caller");
        node!.Path.Should().Be($"{user}/_UserActivity/{nodePath.Replace("/", "_")}");
        node.NodeType.Should().Be("UserActivity");

        // 🚨 The write ran under the CALLER's identity, not system/empty. The handler builds
        // the read (GetQuery, whose per-user RLS captures AccessService.Context eagerly at the
        // call) AND every write under Observable.Using(Impersonate), which re-establishes the
        // caller's context on the tracking + root AccessServices. CreateNode stamps CreatedBy
        // from that context — so a null/system context (the pre-fix RLS-capture bug) would show
        // up here as an empty/"system-security" CreatedBy.
        node.CreatedBy.Should().Be(user,
            "the tracking read+write must run under the caller's identity (Impersonate is in effect " +
            "when GetQuery's RLS captures context and when the create stamps CreatedBy) — never system/empty");
    }

    private async Task OnboardPartitionRoot(string user)
    {
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = user, Name = user });

        await NodeFactory.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Should().Emit();

        await ReadNode(user).Should().Match(n => n is { State: MeshNodeState.Active });
    }

    private async Task<MeshNode?> PollForFirst(string query)
        => await MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(ImmutableList<MeshNode>.Empty, (acc, c) =>
                c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset
                    ? c.Items.ToImmutableList()
                    : acc.AddRange(c.Items))
            .Where(list => list.Count > 0)
            .Select(list => list[0])
            .Should().Within(TimeSpan.FromSeconds(15)).Emit();
}
