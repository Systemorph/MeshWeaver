using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for MeshNode that syncs via IMeshService (reads) and UpdateNodeRequest/DeleteNodeRequest (writes).
/// Loads own node on init, syncs adds/updates/deletes via messages.
/// Saves are debounced: changes are buffered and flushed after 200ms of inactivity.
/// </summary>
public record MeshNodeTypeSource : TypeSourceWithType<MeshNode, MeshNodeTypeSource>
{
    private readonly IStorageService _persistenceCore;
    private readonly string _hubPath;  // e.g., "graph/org1"
    private readonly IWorkspace _workspace;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(200);

    private readonly ConcurrentDictionary<string, MeshNode> _pendingSaves = new();
    private readonly ConcurrentBag<string> _pendingDeletes = new();
    private Timer? _debounceTimer;
    private readonly object _timerLock = new();

    internal MeshNodeTypeSource(IWorkspace workspace, object dataSource, IStorageService persistenceCore, string hubPath)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistenceCore = persistenceCore;
        _hubPath = hubPath;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshNodeTypeSource>>();
        _logger?.LogDebug("MeshNodeTypeSource: Created for hubPath={HubPath}", hubPath);

        TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
            TypeDefinition.CollectionName,
            new KeyFunction(o => ((MeshNode)o).Id, typeof(string)));

        workspace.Hub.RegisterForDisposal((IAsyncDisposable)new FlushOnDispose(this));
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        instances = MergePartialUpdates(instances);

        // Race guard: an empty change can race the async Initialize emission
        // (the framework's stream Subscribe loop may invoke Update with an empty
        // InstanceCollection between stream creation and Initialize completion;
        // see Doc/Architecture/InitializationGates.md). When that happens with
        // _lastSaved already populated, the previous behaviour overwrote
        // _lastSaved = empty and every subsequent ReduceToMeshNode returned null
        // — the OrleansHostedHubRoutingTest workspace-propagation symptom.
        //
        // If incoming is empty AND we have data AND nobody requested an explicit
        // delete (no _pendingDeletes), treat the call as a no-op. Legitimate
        // deletion paths add to _pendingDeletes and pass through unchanged.
        if (instances.Instances.Count == 0
            && _lastSaved.Instances.Count > 0
            && _pendingDeletes.IsEmpty)
        {
            _logger?.LogDebug("MeshNodeTypeSource.UpdateImpl: no-op empty change for {HubPath} (preserving {Count} loaded entries)",
                _hubPath, _lastSaved.Instances.Count);
            return _lastSaved;
        }

        // Open MeshNode init gate when node becomes Active or Transient
        {
            var ownNode = instances.Instances.Values.OfType<MeshNode>()
                .FirstOrDefault(n => n.Path == _hubPath);
            if (ownNode is { State: MeshNodeState.Active or MeshNodeState.Transient })
            {
                _logger?.LogDebug("MeshNodeTypeSource: Opening gate for {HubPath} — node {State} via update", _hubPath, ownNode.State);
                _workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            }
        }

        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && !existing.Equals(x.Value))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        _logger?.LogDebug("MeshNodeTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        var hubVersion = _workspace.Hub.Version;
        foreach (var node in adds.Concat(updates))
        {
            var nodeWithVersion = node with { Version = hubVersion };
            _pendingSaves[node.Path] = nodeWithVersion;
        }

        foreach (var node in deletes)
            _pendingDeletes.Add(node.Path);

        ResetDebounceTimer();

        _lastSaved = instances;
        return instances;
    }

    private void ResetDebounceTimer()
    {
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                // Fire-and-forget for debounce — errors are logged inside FlushPendingSaves.
                // Subscribe drives the flush; no await captures a hub scheduler context.
                FlushPendingSaves().Subscribe(_ => { }, _ => { });
            }, null, DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Flushes the debounced save/delete queue. Returns <see cref="IObservable{Unit}"/> so
    /// callers compose with <c>.Subscribe(...)</c> instead of awaiting a Task — the
    /// awaited continuation captured the calling scheduler and deadlocked when the
    /// timer fired during a grain-context save chain.
    /// <para>
    /// <see cref="IStorageService.SaveNodeAsync"/> / <see cref="IStorageService.DeleteNodeAsync"/>
    /// only touch the DB (in-memory dictionary, file I/O, or SQL) — no hub round-trip,
    /// so wrapping with <see cref="Observable.FromAsync(Func{CancellationToken,Task})"/>
    /// is the sanctioned bridge per <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    private IObservable<System.Reactive.Unit> FlushPendingSaves()
    {
        var saves = new List<MeshNode>();
        foreach (var key in _pendingSaves.Keys.ToArray())
        {
            if (_pendingSaves.TryRemove(key, out var node))
                saves.Add(node);
        }

        var deletes = new List<string>();
        while (_pendingDeletes.TryTake(out var path))
            deletes.Add(path);

        if (saves.Count == 0 && deletes.Count == 0)
            return Observable.Return(System.Reactive.Unit.Default);

        _logger?.LogInformation("MeshNodeTypeSource: Flushing {Saves} saves, {Deletes} deletes for {HubPath}",
            saves.Count, deletes.Count, _hubPath);

        var options = _workspace.Hub.JsonSerializerOptions;

        // Compose all persistence ops as observable steps, fail-soft per item.
        // Concat preserves ordering, mirroring the sequential foreach semantics
        // of the original async/await implementation.
        var saveOps = saves.Select(node =>
            Observable.FromAsync(ct => _persistenceCore.SaveNodeAsync(node, options, ct))
                .Select(_ =>
                {
                    _logger?.LogDebug("MeshNodeTypeSource: Saved {Path} (version={Version})", node.Path, node.Version);
                    return System.Reactive.Unit.Default;
                })
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    _logger?.LogError(ex, "MeshNodeTypeSource: SAVE FAILED for {Path} (version={Version}, hubPath={HubPath}). " +
                        "This may cause data loss on shutdown!", node.Path, node.Version, _hubPath);
                    return Observable.Return(System.Reactive.Unit.Default);
                }));

        var deleteOps = deletes.Select(path =>
            Observable.FromAsync(ct => _persistenceCore.DeleteNodeAsync(path, false, ct))
                .Select(_ =>
                {
                    _logger?.LogDebug("MeshNodeTypeSource: Deleted {Path}", path);
                    return System.Reactive.Unit.Default;
                })
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    _logger?.LogError(ex, "MeshNodeTypeSource: DELETE FAILED for {Path} (hubPath={HubPath})", path, _hubPath);
                    return Observable.Return(System.Reactive.Unit.Default);
                }));

        return saveOps.Concat(deleteOps).Concat()
            .DefaultIfEmpty(System.Reactive.Unit.Default)
            .LastAsync();
    }

    private InstanceCollection MergePartialUpdates(InstanceCollection instances)
    {
        var mergedInstances = new Dictionary<object, object>(instances.Instances);
        var anyMerged = false;

        foreach (var kvp in instances.Instances)
        {
            if (kvp.Value is not MeshNode incomingNode)
                continue;

            if (!_lastSaved.Instances.TryGetValue(kvp.Key, out var existingObj) ||
                existingObj is not MeshNode existingNode)
                continue;

            var isPartialUpdate = incomingNode.Content != null &&
                                  string.IsNullOrEmpty(incomingNode.Name) &&
                                  string.IsNullOrEmpty(incomingNode.Category) &&
                                  string.IsNullOrEmpty(incomingNode.Icon);

            if (!isPartialUpdate)
                continue;

            var mergedNode = existingNode with
            {
                NodeType = incomingNode.NodeType ?? existingNode.NodeType,
                Content = incomingNode.Content ?? existingNode.Content
            };

            mergedInstances[kvp.Key] = mergedNode;
            anyMerged = true;

            _logger?.LogDebug("MeshNodeTypeSource: Merged partial update for {Path}", mergedNode.Path);
        }

        return anyMerged
            ? new InstanceCollection(mergedInstances.Values, TypeDefinition.GetKey)
            : instances;
    }

    /// <summary>
    /// Loads the own MeshNode from persistence on first stream subscription.
    /// <para>
    /// <c>IStorageService.GetNode</c> already returns <see cref="IObservable{T}"/>;
    /// composing through <c>.Select</c> keeps the chain reactive end-to-end. No
    /// <c>await</c>, no <c>.ToTask</c>, no <see cref="Observable.FromAsync{TResult}"/>
    /// — the framework's per-stream init machinery subscribes to this observable
    /// and opens the <see cref="MeshNodeExtensions.MeshNodeInitGateName"/> gate on
    /// first emission. See <c>Doc/Architecture/InitializationGates.md</c>.
    /// </para>
    /// <para>
    /// The gate-open call inside <c>.Select</c> is intentional: the gate is the
    /// "loaded enough to serve queued non-CreateNodeRequest traffic" condition,
    /// and the moment we have that signal is exactly when the observable emits
    /// the first <see cref="InstanceCollection"/>. Hubs that are NOT backed by a
    /// persisted node (top-level mesh hub at <c>mesh/&lt;guid&gt;</c>, deleted
    /// nodes, fresh per-test fixtures) MUST still open the gate — otherwise every
    /// queued response (CreateNodeResponse, GetDataResponse, …) waits forever and
    /// every test times out.
    /// </para>
    /// </summary>
    protected override IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken)
        => _persistenceCore.GetNode(_hubPath, _workspace.Hub.JsonSerializerOptions)
            .FirstAsync()
            .Select(rawNode =>
            {
                var ownNode = rawNode != null ? ResolveJsonElementContent(rawNode) : null;

                if (ownNode is { Version: > 0 })
                {
                    _logger?.LogDebug("MeshNodeTypeSource: Restoring hub {Address} to version {Version}",
                        _workspace.Hub.Address, ownNode.Version);
                    _workspace.Hub.SetInitialVersion(ownNode.Version);
                }

                _workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);

                var allNodes = new List<MeshNode>();
                if (ownNode != null && !string.IsNullOrEmpty(ownNode.Path))
                    allNodes.Add(ownNode);

                _lastSaved = new InstanceCollection(allNodes, node => ((MeshNode)node).Id);
                return _lastSaved;
            });

    private MeshNode ResolveJsonElementContent(MeshNode node)
    {
        if (node.Content is not JsonElement je || je.ValueKind != JsonValueKind.Object)
            return node;

        if (!je.TryGetProperty("$type", out var typeProp))
            return node;

        var typeName = typeProp.GetString();
        if (string.IsNullOrEmpty(typeName))
            return node;

        var registry = _workspace.Hub.TypeRegistry;

        if (!registry.TryGetType(typeName, out var typeDef) || typeDef?.Type == null)
        {
            if (!typeName.Contains('.'))
                return node;

            var shortName = typeName.Split('.').Last();
            if (!registry.TryGetType(shortName, out typeDef) || typeDef?.Type == null)
                return node;
        }

        try
        {
            var raw = je.GetRawText();
            var deserialized = JsonSerializer.Deserialize(raw, typeDef.Type, _workspace.Hub.JsonSerializerOptions);
            if (deserialized != null)
            {
                _logger?.LogDebug("MeshNodeTypeSource: Resolved JsonElement content to {Type} for {Path}",
                    typeDef.Type.Name, node.Path);
                return node with { Content = deserialized };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MeshNodeTypeSource: Failed to resolve JsonElement content for {Path}", node.Path);
        }

        return node;
    }

    private sealed class FlushOnDispose(MeshNodeTypeSource source) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (source._timerLock)
            {
                source._debounceTimer?.Dispose();
                source._debounceTimer = null;
            }

            var pendingCount = source._pendingSaves.Count + source._pendingDeletes.Count;
            if (pendingCount > 0)
            {
                source._logger?.LogInformation(
                    "MeshNodeTypeSource: Disposing with {PendingCount} pending saves for {HubPath} — flushing now",
                    pendingCount, source._hubPath);
            }

            // Bridge the IObservable flush to the IAsyncDisposable contract via .ToTask
            // at the boundary — disposal IS the framework edge, no further mesh work
            // runs after this completes (per AsynchronousCalls.md, the Task boundary
            // belongs at framework lifecycle hooks).
            return new ValueTask(source.FlushPendingSaves()
                .Select(_ =>
                {
                    source._logger?.LogDebug("MeshNodeTypeSource: Disposal flush completed for {HubPath}", source._hubPath);
                    return System.Reactive.Unit.Default;
                })
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    source._logger?.LogError(ex,
                        "MeshNodeTypeSource: DISPOSAL FLUSH FAILED for {HubPath} — pending saves may be lost!", source._hubPath);
                    return Observable.Return(System.Reactive.Unit.Default);
                })
                .ToTask());
        }
    }
}
