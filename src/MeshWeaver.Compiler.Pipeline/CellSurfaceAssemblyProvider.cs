using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The Graph-side half of the pack-scripting seam (issue #1649): resolves every NodeType that
/// declares <see cref="NodeTypeDefinition.CellSurface"/> to its CURRENT baked assembly for one
/// kernel script session. Discovery is the same mesh query the pre-warmer runs
/// (<c>nodeType:NodeType</c>, System-scoped — enumerating type definitions is infrastructure,
/// not a user-attributable read); resolution is the same surface hub activation uses —
/// <see cref="IAssemblyStore.TryGetAssemblyPath"/> keyed by
/// <see cref="NodeTypeDefinition.LastCompiledVersion"/>, loaded through the compilation cache's
/// path-keyed <see cref="NodeAssemblyLoadContext"/> — so a kernel session binds EXACTLY the
/// generation the instance hubs run, and a session opened after a recompile binds the new one.
///
/// <para>Each resolved entry carries a lifetime <see cref="NodeAssemblyLoadContext.Lease"/>;
/// the kernel disposes it with the session, which is what pins a generation for a live session
/// and releases it when the session dies (the pinning semantics documented on
/// <see cref="CellSurfaceAssembly"/>).</para>
///
/// <para>Per-entry failures (missing bytes, a context racing an eviction) SKIP that entry with
/// a Warning — one broken pack must not take the whole cell surface down; only the discovery
/// query itself can fault the resolution, and the kernel handles that per submission.</para>
/// </summary>
internal sealed class CellSurfaceAssemblyProvider(
    IServiceProvider serviceProvider,
    ILogger<CellSurfaceAssemblyProvider> logger)
    : ICellSurfaceAssemblyProvider
{
    /// <summary>Bound on the discovery query — healthy meshes answer in well under a second;
    /// a mesh that cannot enumerate its NodeTypes must fail this session's resolution loudly
    /// (the kernel logs and retries next submission) rather than park the submission.</summary>
    private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<CellSurfaceAssembly>> ResolveCellSurfaceAssemblies()
        => Observable.Defer(() =>
        {
            var meshService = serviceProvider.GetService<IMeshService>();
            var cacheService = serviceProvider.GetService<ICompilationCacheService>();
            if (meshService is null || cacheService is null)
                return Observable.Return<IReadOnlyCollection<CellSurfaceAssembly>>([]);

            var hub = serviceProvider.GetService<IMessageHub>();
            var accessService = serviceProvider.GetService<AccessService>();
            return Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                _ => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{MeshNode.NodeTypePath}"))
                    .Take(1)
                    .Timeout(DiscoveryBudget)
                    .SelectMany(change => ResolveAll(
                        change.Items, cacheService, hub, hub?.JsonSerializerOptions)));
        });

    private IObservable<IReadOnlyCollection<CellSurfaceAssembly>> ResolveAll(
        IReadOnlyCollection<MeshNode> nodes,
        ICompilationCacheService cacheService,
        IMessageHub? hub,
        System.Text.Json.JsonSerializerOptions? jsonOptions)
    {
        NodeTypeDefinition? DefinitionOf(MeshNode? node) => jsonOptions is null
            ? node?.Content as NodeTypeDefinition
            : node.ContentAs<NodeTypeDefinition>(jsonOptions, logger);

        // The QUERY only nominates candidates (a listing — its valid CQRS use). The definition
        // the resolution acts on is re-read AUTHORITATIVELY per candidate below: the index lags
        // writes, and the fields that matter here (CompilationStatus, LastCompiledVersion) are
        // written by the compile watcher moments before a session may resolve them.
        var candidatePaths = nodes
            .Where(n => !string.IsNullOrEmpty(n.Path) && n.State == MeshNodeState.Active)
            .Where(n => DefinitionOf(n)?.CellSurface == true)
            .Select(n => n.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidatePaths.Count == 0)
            return Observable.Return<IReadOnlyCollection<CellSurfaceAssembly>>([]);

        return candidatePaths
            .Select(path => ReadNodeAsSystem(hub, path)
                .SelectMany(node =>
                {
                    // Authoritative read wins over the query copy — including a cleared flag.
                    var definition = DefinitionOf(node);
                    return node is null || definition?.CellSurface != true
                        ? Observable.Return<CellSurfaceAssembly?>(null)
                        : ResolveOne(node, definition, cacheService);
                }))
            .Merge()
            .Where(resolved => resolved is not null)
            .Select(resolved => resolved!)
            .ToList()
            .Select(resolved => (IReadOnlyCollection<CellSurfaceAssembly>)resolved);
    }

    /// <summary>
    /// Authoritative single-node read under the System identity. The scope is established at
    /// SUBSCRIBE time (Observable.Using) because this is subscribed from a continuation of the
    /// discovery query — the ambient AsyncLocal identity does not survive that hop (the same
    /// rule and shape as <c>MeshNodeCompilationService.ReadCompileSourceNode</c>).
    /// </summary>
    private IObservable<MeshNode?> ReadNodeAsSystem(IMessageHub? hub, string path)
    {
        if (hub is null)
            return Observable.Return<MeshNode?>(null);
        var accessService = serviceProvider.GetService<AccessService>();
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
            _ => hub.GetMeshNode(path, TimeSpan.FromSeconds(15), ReadTimeoutBehavior.EmitNull))
            .Take(1);
    }

    private IObservable<CellSurfaceAssembly?> ResolveOne(
        MeshNode node, NodeTypeDefinition definition, ICompilationCacheService cacheService)
    {
        // A framework-resident (static) NodeType's assembly ships with the process and is
        // already in the kernel's baseline snapshot — nothing to add, nothing to lease.
        if (string.Equals(definition.LatestAssemblyCollection, FrameworkAssemblyStore.CollectionName,
                StringComparison.Ordinal))
            return Observable.Return<CellSurfaceAssembly?>(null);

        // ── #2820 — the execute-time interlock, second enforcement site. ──────────────────────
        // This one is NOT reachable from the per-instance-hub gate in NodeTypeEnrichmentHelpers:
        // the cell surface loads the assembly straight through NodeAssemblyLoadContext, with no
        // enrichment and no HubConfiguration, and from that moment every script submission in the
        // session can call the pack's functions by bare name — with full write access through the
        // ordinary mesh APIs available to scripts. It is the most directly "armed" surface there
        // is, so it gets its own check rather than an inherited one.
        //
        // 🚨 Refuses ONE state (AdoptionRefused). AdoptedUnverified joins normally — a legacy
        // bundle's provenance is unknown, not proven stale, and refusing it would silently empty
        // the cell surface of every pack published before producers recorded a fingerprint.
        if (NodeTypeExecutionGate.RefusesExecution(definition))
        {
            // Error, not Warning: the surrounding per-entry skips are availability problems that
            // resolve themselves; this one is a verdict about the bytes that only a rebuild or a
            // rebake changes.
            logger.LogError(
                "{Summary} It is NOT being joined into this kernel session's cell surface. {Recovery}",
                NodeTypeExecutionGate.RefusalSummary(node.Path, definition),
                NodeTypeExecutionGate.RecoveryVerb);
            return Observable.Return<CellSurfaceAssembly?>(null);
        }

        if (definition.CompilationStatus is not CompilationStatus.Ok
            || definition.LastCompiledVersion is null)
        {
            logger.LogWarning(
                "Cell-surface NodeType {NodeTypePath} has no usable build "
                + "(CompilationStatus={Status}, LastCompiledVersion={Version}) — not joining the cell surface",
                node.Path, definition.CompilationStatus, definition.LastCompiledVersion);
            return Observable.Return<CellSurfaceAssembly?>(null);
        }

        var store = serviceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        // The assembly load is a blocking file/PE leaf — route it through the shared Compile
        // pool, the same gate NodeType compilation and the kernel's Roslyn pass use.
        var pool = serviceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
            ?? IoPool.Unbounded;

        return store.TryGetAssemblyPath(node.Path, definition.LastCompiledVersion.Value)
            .Take(1)
            .SelectMany(localPath =>
            {
                if (string.IsNullOrEmpty(localPath))
                {
                    logger.LogWarning(
                        "Cell-surface NodeType {NodeTypePath} v{Version}: assembly store has no bytes — not joining the cell surface",
                        node.Path, definition.LastCompiledVersion);
                    return Observable.Return<CellSurfaceAssembly?>(null);
                }
                return pool.InvokeBlocking(_ => LoadLeased(node.Path, localPath!, cacheService));
            })
            // A single pack's failure (evicted context, corrupt bytes) must not take the whole
            // cell surface down — skip it loudly; the rest of the set still resolves.
            .Catch<CellSurfaceAssembly?, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "Cell-surface NodeType {NodeTypePath}: assembly resolution failed — not joining the cell surface",
                    node.Path);
                return Observable.Return<CellSurfaceAssembly?>(null);
            });
    }

    /// <summary>
    /// Loads the assembly through the compilation cache's path-keyed context (the SAME context
    /// instance hubs share for this generation) and takes a lifetime lease on it BEFORE loading,
    /// so a concurrent supersede-eviction cannot unload the generation between load and use.
    /// </summary>
    private CellSurfaceAssembly? LoadLeased(
        string nodeTypePath, string localPath, ICompilationCacheService cacheService)
    {
        var nodeName = cacheService.SanitizeNodeName(nodeTypePath);
        var context = cacheService.GetOrCreateLoadContextForPath(nodeName, localPath);
        var lease = context.Lease();
        try
        {
            var assembly = context.LoadNodeAssembly();
            if (assembly is null)
            {
                lease.Dispose();
                logger.LogWarning(
                    "Cell-surface NodeType {NodeTypePath}: no assembly could be loaded from {LocalPath}",
                    nodeTypePath, localPath);
                return null;
            }
            return new CellSurfaceAssembly(nodeTypePath, localPath, assembly, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
