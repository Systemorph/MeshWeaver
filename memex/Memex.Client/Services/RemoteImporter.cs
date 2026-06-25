using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Client.Services;

/// <summary>
/// Pulls mesh nodes from a connected remote <see cref="MemexInstance"/> into the local in-process mesh.
///
/// <para><b>Why MCP (<see cref="IRemoteMeshClient"/>) and not the SignalR connection:</b> the framework
/// FORBIDS reading a raw remote <c>MeshNode</c> over the workspace sync protocol —
/// <c>Workspace.GetRemoteStream&lt;MeshNode&gt;</c> throws ("the single-node remote reduce does not
/// converge"), and the only sanctioned cross-mesh sync stream
/// (<c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;</c>, used by <c>GetMenu</c>) yields rendered
/// layout-area controls, not the node content/type a faithful subtree copy needs. The framework's
/// purpose-built cross-instance node read surface is <see cref="IRemoteMeshClient"/> (Get / SearchPaths),
/// the same one <c>MirrorRequest</c>'s Pull uses. It authenticates with the instance's already-stored
/// token against the remote portal's <c>/mcp</c> endpoint — no new credential, no SignalR coupling.</para>
///
/// <para><b>Reactive end-to-end</b> — every method returns <see cref="IObservable{T}"/>; the caller
/// Subscribes. The local write side is the canonical idempotent verb <see cref="CreateOrUpdateNodeRequest"/>
/// (create when absent, update when present → re-import never duplicates), exactly as
/// <c>NodeCopyHelper.CopyNodeTree</c> writes a copied subtree. Mesh-scoped singleton — the per-URL
/// <see cref="IRemoteMeshClient"/> handles are instance fields (no static state) and dispose with the mesh.</para>
/// </summary>
public sealed class RemoteImporter : IAsyncDisposable
{
    /// <summary>Bounded fan-out for the per-node remote Get / local upsert — never opens every per-node
    /// hub on the receiving side at once (matches <c>NodeCopyHelper.DefaultBatchSize</c> intent).</summary>
    private const int Concurrency = 8;

    /// <summary>The remote <c>search</c> tool caps at 50 results; mirror that ceiling for the browse list.</summary>
    private const int SearchLimit = 50;

    /// <summary>Default browse query — the remote's top-level main nodes (satellites excluded).</summary>
    public const string DefaultQuery = "is:main";

    private readonly IMessageHub _hub;
    private readonly IRemoteMeshClientFactory _factory;
    private readonly ILogger<RemoteImporter>? _logger;

    // One MCP client per remote URL, reused across Search/Import (its connect handshake is promise-cached).
    // Instance field on a mesh-scoped singleton — disposed with the mesh, never static.
    private readonly ConcurrentDictionary<string, IRemoteMeshClient> _clients = new();

    public RemoteImporter(IMessageHub hub, IRemoteMeshClientFactory factory)
    {
        _hub = hub;
        _factory = factory;
        _logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<RemoteImporter>();
    }

    private IRemoteMeshClient Client(MemexInstance instance)
        => _clients.GetOrAdd(instance.Url, url => _factory.Create(url, instance.Token!));

    /// <summary>
    /// Runs <paramref name="query"/> (GitHub-style mesh syntax; defaults to <see cref="DefaultQuery"/>)
    /// against the REMOTE mesh and emits the matched nodes — paths from the remote <c>search</c> tool,
    /// hydrated via <c>get</c>. Single emission then completes. A failed individual <c>get</c> drops that
    /// node and continues (logged) rather than failing the whole search.
    /// </summary>
    public IObservable<IEnumerable<MeshNode>> Search(MemexInstance instance, string? query)
    {
        if (instance is null || string.IsNullOrEmpty(instance.Token))
            return Observable.Return((IEnumerable<MeshNode>)Array.Empty<MeshNode>());

        var q = string.IsNullOrWhiteSpace(query) ? DefaultQuery : query!.Trim();
        var client = Client(instance);

        return client.SearchPaths(q).Take(1)
            .SelectMany(paths =>
            {
                var take = paths.Take(SearchLimit).ToArray();
                if (take.Length == 0)
                    return Observable.Return((IEnumerable<MeshNode>)Array.Empty<MeshNode>());
                return take
                    .Select(p => client.Get(p).Take(1)
                        .Catch<MeshNode?, Exception>(ex =>
                        {
                            _logger?.LogWarning(ex, "remote Get failed for {Path}", p);
                            return Observable.Return<MeshNode?>(null);
                        }))
                    .Merge(Concurrency)
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToList()
                    .Select(list => (IEnumerable<MeshNode>)list);
            });
    }

    /// <summary>
    /// Imports each selected root path AND its descendant subtree from <paramref name="instance"/> into the
    /// local mesh under <paramref name="targetNamespace"/>, preserving each node's Id, NodeType, and Content.
    /// Emits a RUNNING TOTAL of successfully upserted nodes (one bump per node) so the UI shows live progress;
    /// the final emission is the grand total. Idempotent: re-importing updates the same target paths rather
    /// than duplicating (create-or-update). Per-node failures are logged and counted as 0 — they never abort
    /// the run.
    /// </summary>
    public IObservable<int> Import(MemexInstance instance, IReadOnlyList<string> rootPaths, string? targetNamespace)
    {
        if (instance is null || string.IsNullOrEmpty(instance.Token) || rootPaths is null || rootPaths.Count == 0)
            return Observable.Return(0);

        var client = Client(instance);
        var targetNs = (targetNamespace ?? string.Empty).Trim().Trim('/');

        // Capture the caller's identity (the device user — set as the circuit context in MauiProgram) HERE,
        // on the calling thread, and stamp it on every cross-hub write. The upserts subscribe deep inside
        // .SelectMany/.Merge lambdas that run on the MCP I/O-pool emission threads where the AsyncLocal
        // AccessContext is gone, so without the explicit stamp the owner's PostPipeline would fail closed.
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;

        return rootPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(root => ImportSubtree(client, root.Trim(), targetNs, ctx))
            .Concat()                                   // one root subtree at a time
            .Scan(0, (acc, inc) => acc + inc);          // running total → progress
    }

    private IObservable<int> ImportSubtree(IRemoteMeshClient client, string root, string targetNs, AccessContext? ctx)
    {
        // Re-root relative to the root node's own namespace, exactly like NodeCopyHelper.RemapPath: the
        // selected node keeps its Id under the local target namespace (e.g. rbuergi/Story → {target}/Story).
        var rootNs = root.Contains('/') ? root[..root.LastIndexOf('/')] : string.Empty;

        return client.SearchPaths($"path:{root} scope:subtree").Take(1)
            .SelectMany(paths =>
            {
                var all = paths.ToList();
                if (!all.Contains(root)) all.Add(root);   // ensure the root itself is included
                if (all.Count == 0) return Observable.Return(0);
                return all
                    .Select(p => UpsertFromRemote(client, p, rootNs, targetNs, ctx))
                    .Merge(Concurrency);
            });
    }

    private IObservable<int> UpsertFromRemote(
        IRemoteMeshClient client, string sourcePath, string rootNs, string targetNs, AccessContext? ctx)
        => client.Get(sourcePath).Take(1).SelectMany(remote =>
        {
            if (remote is null) return Observable.Return(0);

            var newPath = RemapPath(remote.Path, rootNs, targetNs);
            // Build the local node from the remote one, preserving content/type — same shape as
            // NodeCopyHelper.CopyOne. FromPath splits newPath into (Id, Namespace).
            var node = MeshNode.FromPath(newPath) with
            {
                Name = remote.Name,
                NodeType = remote.NodeType,
                Icon = remote.Icon,
                Category = remote.Category,
                Content = remote.Content,
                State = MeshNodeState.Active,
                PreRenderedHtml = remote.PreRenderedHtml,
            };

            return _hub.Observe<CreateOrUpdateNodeResponse>(
                    new CreateOrUpdateNodeRequest(node),
                    o => ctx is null ? o : o.WithAccessContext(ctx))
                .FirstAsync()
                .Select(d => d.Message)
                .Select(resp =>
                {
                    if (resp.Success) return 1;
                    _logger?.LogWarning("upsert of {Path} failed: {Error}", newPath, resp.Error);
                    return 0;
                })
                .Catch<int, Exception>(ex =>
                {
                    _logger?.LogWarning(ex, "upsert of {Path} threw", newPath);
                    return Observable.Return(0);
                });
        });

    // Mirrors NodeCopyHelper.RemapPath: strip the source namespace prefix, then re-root under the target.
    private static string RemapPath(string path, string sourceNamespace, string targetNamespace)
    {
        var relative =
            string.IsNullOrEmpty(sourceNamespace) ? path
            : path.StartsWith(sourceNamespace + "/", StringComparison.Ordinal) ? path[(sourceNamespace.Length + 1)..]
            : path;
        return string.IsNullOrEmpty(targetNamespace) ? relative : $"{targetNamespace}/{relative}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            if (client is IAsyncDisposable d)
                try { await d.DisposeAsync(); }
                catch (Exception ex) { _logger?.LogWarning(ex, "remote client dispose failed"); }
        _clients.Clear();
    }
}
