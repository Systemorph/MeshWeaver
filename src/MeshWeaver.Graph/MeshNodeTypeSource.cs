using System.Reactive.Linq;
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
/// TypeSource for MeshNode that bridges a routing-supplied own-node observable
/// into the workspace and dispatches workspace writes:
/// <list type="bullet">
///   <item><b>Adds (create) and Deletes</b> go straight to <see cref="IStorageService"/>
///   — insta write, no queue. These are infrequent and ordering matters
///   (descendants must see parent state immediately).</item>
///   <item><b>Updates</b> are dispatched through the per-node hub's actor inbox
///   as <see cref="SaveMeshNodeRequest"/> messages — the handler in
///   <see cref="MeshDataSourceExtensions"/> calls <c>SaveNode</c>. Updates from
///   editing surfaces can fire in rapid succession; the inbox serialises them
///   per node without blocking the workspace pipeline.</item>
/// </list>
/// </summary>
public record MeshNodeTypeSource : TypeSourceWithType<MeshNode, MeshNodeTypeSource>
{
    private readonly IStorageService? _persistenceCore;
    private readonly string _hubPath;  // e.g., "graph/org1"
    private readonly IWorkspace _workspace;
    private readonly ILogger? _logger;
    private readonly IObservable<MeshNode>? _ownNodeStream;
    private InstanceCollection _lastSaved = new();

    internal MeshNodeTypeSource(
        IWorkspace workspace,
        object dataSource,
        IStorageService? persistenceCore,
        string hubPath,
        IObservable<MeshNode>? ownNodeStream = null)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistenceCore = persistenceCore;
        _hubPath = hubPath;
        // DistinctUntilChanged drops re-emissions that already match the local
        // state (same Version + content) — the routing layer (catalog stream /
        // change feed) can re-publish the same node multiple times after a
        // local write echoes through persistence; we don't want each echo to
        // re-run the workspace pipeline. Replay(1).RefCount() so late
        // subscribers (OwnNodeCache, future consumers) see the latest
        // authoritative snapshot, not just future emissions.
        _ownNodeStream = ownNodeStream?
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshNodeTypeSource>>();
        _logger?.LogDebug("MeshNodeTypeSource: Created for hubPath={HubPath} (routingSuppliedStream={Supplied})",
            hubPath, ownNodeStream != null);

        TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
            TypeDefinition.CollectionName,
            new KeyFunction(o => ((MeshNode)o).Id, typeof(string)));
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
        if (instances.Instances.Count == 0 && _lastSaved.Instances.Count > 0)
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
        var options = _workspace.Hub.JsonSerializerOptions;

        // Creates: insta write through IStorageService — descendants and
        // satellites depend on the parent existing, so we don't queue.
        foreach (var node in adds)
        {
            var nodeWithVersion = node with { Version = hubVersion };
            if (_persistenceCore is null)
                continue;
            _persistenceCore.SaveNode(nodeWithVersion, options).Subscribe(
                _ => _logger?.LogDebug("MeshNodeTypeSource: Created {Path} (version={Version})",
                    nodeWithVersion.Path, nodeWithVersion.Version),
                ex => _logger?.LogError(ex, "MeshNodeTypeSource: CREATE FAILED for {Path}", nodeWithVersion.Path));
        }

        // Updates do NOT dispatch saves here — the persistence subscriber on
        // the workspace's MeshNode stream (registered by SubscribeToOwnDeletion
        // in MeshDataSource) handles updates with Sample(200ms) debouncing.
        // The diff bookkeeping above keeps _lastSaved current so create/delete
        // detection on subsequent calls is accurate.

        // Deletes: insta write — the per-node hub is the authoritative source,
        // and the parent's children-list reads should see the absence
        // immediately on next query.
        foreach (var node in deletes)
        {
            if (string.IsNullOrEmpty(node.Path) || _persistenceCore is null)
                continue;
            var path = node.Path;
            _persistenceCore.DeleteNode(path, recursive: false).Subscribe(
                _ => _logger?.LogDebug("MeshNodeTypeSource: Deleted {Path}", path),
                ex => _logger?.LogError(ex, "MeshNodeTypeSource: DELETE FAILED for {Path}", path));
        }

        _lastSaved = instances;
        return instances;
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
    /// Seeds the workspace with the own MeshNode and follows subsequent emissions.
    /// <para>
    /// The routing layer (Orleans <c>MessageHubGrain</c>, Monolith
    /// <c>MonolithRoutingService</c>) attaches an own-node observable via
    /// <see cref="OwnNodeStreamExtensions.WithOwnNodeStream"/> at hub
    /// instantiation; that stream is the source of truth — its first emission
    /// seeds the workspace and subsequent emissions push live updates into the
    /// MeshNodeReference reducer without a separate change-feed subscription.
    /// </para>
    /// <para>
    /// When no stream is supplied (fixtures that bypass routing), falls back
    /// to a one-shot persistence read so tests that construct a per-node hub
    /// directly continue to work.
    /// </para>
    /// </summary>
    protected override IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken)
    {
        if (_ownNodeStream is not null)
        {
            return _ownNodeStream
                .Where(n => n != null)
                .Select(rawNode => BuildInstanceCollection(rawNode));
        }

        if (_persistenceCore is not null)
        {
            return _persistenceCore.GetNode(_hubPath, _workspace.Hub.JsonSerializerOptions)
                .Select(rawNode => BuildInstanceCollection(rawNode));
        }

        // No stream and no persistence — emit empty and open the gate so
        // queued CreateNodeRequest / GetDataResponse traffic isn't held forever.
        _workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
        return Observable.Return(_lastSaved);
    }

    private InstanceCollection BuildInstanceCollection(MeshNode? rawNode)
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
    }

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
}
