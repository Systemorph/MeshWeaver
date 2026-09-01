using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// <para>Two lanes feed it: the <see cref="ContentTypeRegistrationSweep"/> hosted service walks
/// the STATIC definitions (<c>AddMeshNodes</c>) at start, and
/// <see cref="EnsureRegisteredForCompiledDefinition"/> is invoked wherever a COMPILED definition
/// flows through <c>MeshNodeTypeSource</c> — which covers boot hydration and every later
/// recompile without a timer or a second enumeration.</para>
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
            var probeAddress = new Address($"content-type-registration/{Guid.NewGuid():N}");
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

    /// <summary>
    /// The DYNAMIC lane: a compiled NodeType definition flowed through the workspace — register
    /// its content types if the registry does not know its path yet. Resolves the compiled
    /// HubConfiguration from the already-cached assembly (no compile is triggered), then runs the
    /// same probe the static lane uses. Fire-safe: subscribed with an error arm, once per
    /// definition emission; the registry pre-check bounds it to one probe per type per process.
    /// </summary>
    /// <param name="hub">The hub the definition flowed through (its services resolve the
    /// compilation service and assembly store).</param>
    /// <param name="definition">The NodeType definition node.</param>
    public static void EnsureRegisteredForCompiledDefinition(IMessageHub hub, MeshNode definition)
    {
        if (definition.Content is not NodeTypeDefinition def
            || def.CompilationStatus != CompilationStatus.Ok
            || string.IsNullOrEmpty(def.LatestAssemblyPath))
            return;
        var registry = hub.ServiceProvider.GetService<IMeshContentTypeRegistry>();
        if (registry is null || registry.TryResolveByNodeType(definition.Path, out _))
            return;
        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        var store = hub.ServiceProvider.GetService<IAssemblyStore>();
        if (compilationService is null || store is null)
            return;
        var logger = hub.ServiceProvider.GetService<ILogger<MeshNodeTypeSource>>();
        var version = def.LastCompiledVersion ?? definition.Version;
        store.TryGetAssemblyPath(definition.Path, version)
            .Take(1)
            .SelectMany(localPath => string.IsNullOrEmpty(localPath)
                ? Observable.Empty<NodeCompilationResult?>()
                : compilationService.GetConfigurationsFromExistingAssembly(localPath!, definition.Path).Take(1))
            .Subscribe(
                result =>
                {
                    var cfg = result?.NodeTypeConfigurations
                        .FirstOrDefault(c =>
                            string.Equals(c.NodeType, definition.Path, StringComparison.OrdinalIgnoreCase))
                        ?.HubConfiguration;
                    if (cfg is not null)
                        ProbeRegister(hub, definition.Path, cfg, logger);
                },
                ex => logger?.LogDebug(ex,
                    "Content-type registration skipped for compiled NodeType {NodeType}",
                    definition.Path));
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
