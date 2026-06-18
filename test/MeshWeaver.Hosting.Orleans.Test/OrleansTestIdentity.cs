using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// DevLogin analog for Orleans test clusters (mirrors
/// <c>MonolithMeshTestBase.InitializeAsync</c> → <c>TestUsers.DevLogin(Mesh)</c>).
///
/// <para>The never-null AccessContext invariant
/// (<c>feedback_access_context_always_set</c>) fails a non-exempt post that carries
/// no identity. In production, a client/portal stamps the circuit user and that
/// identity flows per-message over the wire to the silo grain. Orleans tests bypass
/// that: they post <c>PingRequest</c>/<c>CreateNodeRequest</c> directly from a
/// client/portal/silo mesh hub whose <see cref="AccessService.CircuitContext"/> is
/// null, so the gate fails the delivery.</para>
///
/// <para>This seeds a default circuit identity once per mesh hub. <see cref="AccessService"/>
/// is a mesh-wide singleton shared by every hosted hub (a hosted hub re-uses the
/// parent's instance — see <c>MessageHubConfiguration.ConfigureServices</c>), so one
/// seed per mesh covers all of its client/portal/node hubs. It is a pure
/// <see cref="AccessService.CircuitContext"/> <i>fallback</i>: the post pipeline only
/// reaches it when a delivery has no per-message <c>AccessContext</c>, so it never
/// overrides the real client→silo identity flow that passing tests rely on.</para>
///
/// <para>The seeded identity is the well-known <see cref="WellKnownUsers.System"/>
/// principal — granted <c>Permission.All</c> unconditionally, so a direct seed post
/// passes RLS in every cluster regardless of which (if any) <c>AccessAssignment</c>
/// nodes that cluster provisions. This is the test-layer analog of
/// <c>AccessService.ImpersonateAsSystem</c> (the same identity
/// <c>MonolithMeshTestBase.SeedTopLevel</c> provisions data under), NOT a change to
/// any hub's production posting identity.</para>
/// </summary>
internal static class OrleansTestIdentity
{
    private static readonly AccessContext System = new()
    {
        ObjectId = WellKnownUsers.System,
        Name = WellKnownUsers.System,
    };

    /// <summary>
    /// Seeds the default <see cref="WellKnownUsers.System"/> circuit identity on the
    /// cluster's client mesh hub (when present) and every silo's mesh hub. Call once
    /// after <see cref="TestCluster.DeployAsync"/>.
    /// </summary>
    public static void SeedDefaultIdentity(TestCluster cluster)
    {
        // Client mesh hub — covers client/* and portal/* hosted hubs. Absent on
        // client-less fixtures (e.g. TwoSiloCacheUpdateFixture adds no client
        // configurator, so the default client has no IMessageHub registered).
        if (cluster.Client is { ServiceProvider: { } clientServices })
            SeedHub(clientServices.GetService<IMessageHub>());

        // Each silo's mesh hub — covers direct silo-hub posts and the silo mesh
        // hub's own internal re-posts.
        foreach (var silo in cluster.Silos)
            SeedHub(GetSiloMeshHub(silo));
    }

    private static void SeedHub(IMessageHub? mesh) =>
        mesh?.ServiceProvider.GetService<AccessService>()?.SetCircuitContext(System);

    private static IMessageHub? GetSiloMeshHub(SiloHandle silo)
    {
        // InProcessSiloHandle exposes SiloHost (IHost). Reflection mirrors
        // TwoSiloCacheUpdateFixture.PrimarySiloMeshHub and keeps this resilient to
        // the concrete handle type without a hard cast.
        var siloHost = silo.GetType().GetProperty("SiloHost")?.GetValue(silo) as IHost;
        return siloHost?.Services.GetService<IMessageHub>();
    }
}
