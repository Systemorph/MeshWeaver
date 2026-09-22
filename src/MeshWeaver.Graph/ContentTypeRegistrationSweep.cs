using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// 🚨 <b>A NodeType's content type registers because the DEFINITION is known — never because an
/// instance happens to exist.</b>
///
/// <para>Registration used to live exclusively inside the type's HubConfiguration
/// (<see cref="MeshDataSource.WithContentType(Type)"/>), which runs when an instance hub
/// cold-activates. On a portal where a type is defined and compiled but has no live instance, the
/// mesh-wide <see cref="IMeshContentTypeRegistry"/> therefore never learned the content type, and
/// every read seam degraded that <c>$type</c> to an untyped JsonElement — by design, for what
/// looked like an unknown type.</para>
///
/// <para><b>Measured on a real portal, 2026-09-01:</b> zero nodes carried
/// <c>nodeType: Store/Plugin</c> (installed course roots are re-typed to <c>Space</c>), so
/// <c>PluginContent</c> was never registered; every Store cover computed NO action buttons — no
/// Get, no Install, no Update — and with the Update lane dead, installed course content became
/// unrefreshable. One missing registration disabled the whole commerce surface. Deployments with
/// live instances (the cloud) escaped by accident, which is what let the gap hide.</para>
///
/// <para><b>The mechanism is the one the schema probes already use:</b> building a short-lived
/// probe hub with the type's HubConfiguration executes <c>WithContentType</c> during the
/// data-context build — registration is the build's side effect — and
/// <c>AsTransientNodeProbe(startDataSources: false)</c> starts nothing (no sync/ streams, no
/// control plane; see <c>MeshOperations.ReadFromContentType</c>, whose probe this mirrors). Cost
/// is a config build per type, once per process.</para>
///
/// <para>🚨 "No control plane" was aspirational for one watcher until #2990: the Activity Control
/// Plane is installed by the ADOPTER (<c>KernelContainer</c> and every Activity-shaped NodeType,
/// from their own <c>WithInitialization</c>), not by <c>MeshDataSource</c>, so every sweep of such
/// a type wrote an ERROR-level <c>ActivityControlPlane subscription faulted on
/// content-type-registration/… — re-establishing</c> and armed a re-establish against a hub that
/// was already gone. The guard now lives at the shared seam
/// (<c>ActivityControlPlaneExtensions.WatchControlPlane</c>/<c>WatchSubmission</c>) and at the
/// own-node read seam (<c>MeshNodeStreamHandle.Subscribe</c> answers a probe's own address with an
/// empty stream), so an adopter cannot reinstate it. See
/// <c>Doc/Architecture/TransientNodeProbes</c>.</para>
///
/// <para>Scope: the <see cref="ContentTypeRegistrationSweep"/> hosted service walks the STATIC
/// definitions (<c>AddMeshNodes</c>) at start — which is where the measured defect lived: every
/// commerce content type is a static definition. COMPILED (dynamic) types are deliberately NOT
/// swept: their configuration lives inside an assembly, and eagerly opening every compiled
/// type's bytes at boot is exactly the per-NodeType cost the CI content bake removed (#1660 —
/// 13.5 s of a 101 s boot); worse, a probe of an adopted-but-not-yet-loadable bake trips the
/// loader's corrupt-file self-heal, which DELETES the store's bytes and forces a re-adoption
/// (ShippedPrebuiltBundlesTest pins that boot contract). A dynamic type registers the moment an
/// instance hub activates.</para>
///
/// <para>🚨 <b>That last clause used to end "…and a dynamic type with zero instances has no
/// payload carrying its discriminator, so there is nothing to degrade", and the premise is false
/// for a type with FEW instances</b> (Systemorph/MeshWeaver.Plugins#2178, Systemorph/MeshWeaver.Plugins#2180). A per-node hub is
/// a single activation cluster-wide, so a type whose handful of instances all activate on ANOTHER
/// replica registers there and nowhere else — while every other replica reads those nodes through
/// its own stream cache and cannot bind the <c>$type</c>. Measured on two live portals 2026-09-21:
/// 8-11 such types per replica, a different set on each, <c>Hosting/Deployment</c> and
/// <c>Hosting/DeploymentStatus</c> among them, every one of them carrying a usable assembly. The
/// boot scope here is unchanged; what is corrected is the REASON, because the old one reads as
/// "nothing is exposed" and something is.</para>
///
/// <para>🚨 <b>Do NOT close it by calling <see cref="ProbeRegister"/> from a degrade seam.</b>
/// Getting a dynamic type's configuration means
/// <c>IMeshNodeHubFactory.ResolveHubConfiguration</c> → <c>NodeTypeEnrichmentHelpers</c>, which
/// TRIGGERS THE COMPILATION CHAIN and can WRITE the shared NodeType record (the stale-Ok self-heal
/// flips it to <c>Pending</c> to force a recompile) and arms the rebind watcher. A read seam that
/// did this would let every non-owning replica independently compile and re-stamp one record that
/// the whole deployment shares — the cross-stamp <c>NodeTypeLiveRecordCensus</c> exists to DETECT.
/// That is a reading of the code and it is the whole case: a test failure that looked like
/// confirmation was withdrawn when the same test failed with the seam removed. Registration belongs with the
/// component already sanctioned to activate dynamic types on this replica — the pre-warmer, whose
/// <c>AlreadyBaked</c> branch is where the skip happens — not with whoever reads a node. See
/// <c>Doc/Architecture/DynamicContentTypeRegistration</c>.</para>
/// </summary>
public static class ContentTypeRegistration
{
    /// <summary>
    /// Builds (and immediately disposes) the registration probe for <paramref name="nodeTypePath"/>:
    /// the config build runs <c>WithContentType</c>, which records the content type in the
    /// mesh-wide registry under the stamped NodeType path. Failures are logged at Debug and
    /// swallowed — an unregistrable type is exactly as readable as it was before this existed.
    /// </summary>
    /// <param name="meshHub">Hub used to host the transient probe.</param>
    /// <param name="nodeTypePath">The NodeType path being registered.</param>
    /// <param name="hubConfig">The type's hub configuration.</param>
    /// <param name="logger">Debug-level diagnostics.</param>
    public static void ProbeRegister(
        IMessageHub meshHub,
        string nodeTypePath,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfig,
        ILogger? logger)
    {
        try
        {
            // 🚨 Minted FROM the shared constant, never as a literal: the read seams that answer a
            // probe's own address directly (MeshNodeStreamCache.GetStreamRaw) key off
            // TransientProbeAddresses.IsProbeAddress, and a producer whose prefix is not in that
            // predicate is a producer those guards silently skip (#2894 → #2990).
            var probeAddress = new Address(
                $"{TransientProbeAddresses.ContentTypeRegistrationProbePrefix}{Guid.NewGuid():N}");
            var probeHub = meshHub.GetHostedHub(
                probeAddress,
                c => hubConfig(c.WithNodeTypePath(nodeTypePath))
                    .AsTransientNodeProbe(startDataSources: false));
            probeHub?.Dispose();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "Content-type registration probe failed for NodeType {NodeType} — content of this "
                + "type stays untyped on hubs that have not registered it themselves",
                nodeTypePath);
        }
    }

}

/// <summary>
/// The STATIC lane of <see cref="ContentTypeRegistration"/>: at start, walk every static node
/// definition (<c>AddMeshNodes</c>) that carries a HubConfiguration and register its content
/// types, so a defined-but-never-instantiated type is readable everywhere from the first request.
/// Deliberately synchronous inside <see cref="StartAsync"/> — the builds are config-only and the
/// determinism is what a test (and a first request) relies on.
/// </summary>
public sealed class ContentTypeRegistrationSweep(IServiceProvider services) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var hub = services.GetService<IMessageHub>();
        var registry = services.GetService<IMeshContentTypeRegistry>();
        if (hub is null || registry is null)
            return Task.CompletedTask;
        var logger = services.GetService<ILogger<ContentTypeRegistrationSweep>>();
        var swept = 0;
        foreach (var node in services.EnumerateStaticNodes())
        {
            if (node.HubConfiguration is not { } cfg
                || registry.TryResolveByNodeType(node.Path, out _))
                continue;
            ContentTypeRegistration.ProbeRegister(hub, node.Path, cfg, logger);
            swept++;
        }
        if (swept > 0)
            logger?.LogInformation(
                "Content-type registration sweep: {Count} static NodeType definition(s) probed — "
                + "their content types resolve without any instance existing.",
                swept);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Wires the <see cref="ContentTypeRegistrationSweep"/> — called by each transport's
/// server registry, the same placement as the root-hub reply stream.</summary>
public static class ContentTypeRegistrationSweepExtensions
{
    /// <summary>Adds the static-definition content-type registration sweep to the host.</summary>
    /// <param name="services">The service collection to add the hosted sweep to.</param>
    public static IServiceCollection AddContentTypeRegistrationSweep(this IServiceCollection services)
    {
        services.AddHostedService<ContentTypeRegistrationSweep>();
        return services;
    }
}
