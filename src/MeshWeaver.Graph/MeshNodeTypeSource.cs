using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
            new KeyFunction(o => ((MeshNode)o).Path, typeof(string)));

        workspace.Hub.RegisterForDisposal(new FlushOnDispose(this));
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        instances = MergePartialUpdates(instances);

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
            _debounceTimer = new Timer(_ => FlushPendingSaves(), null, DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPendingSaves()
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
            return;

        _logger?.LogDebug("MeshNodeTypeSource: Flushing {Saves} saves, {Deletes} deletes for {HubPath}",
            saves.Count, deletes.Count, _hubPath);

        var options = _workspace.Hub.JsonSerializerOptions;
        foreach (var node in saves)
        {
            try
            {
                // Save directly to persistence — do NOT post UpdateNodeRequest
                // to avoid a feedback loop (handler → workspace → TypeSource → handler)
                _ = _persistenceCore.SaveNodeAsync(node, options);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MeshNodeTypeSource: Save failed for {Path}", node.Path);
            }
        }

        foreach (var path in deletes)
        {
            try
            {
                _ = _persistenceCore.DeleteNodeAsync(path);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MeshNodeTypeSource: Delete failed for {Path}", path);
            }
        }
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

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        var ownNode = await _persistenceCore.GetNodeAsync(_hubPath, _workspace.Hub.JsonSerializerOptions, ct);

        if (ownNode != null)
            ownNode = ResolveJsonElementContent(ownNode);

        if (ownNode is { Version: > 0 })
        {
            _logger?.LogDebug("MeshNodeTypeSource: Restoring hub {Address} to version {Version}",
                _workspace.Hub.Address, ownNode.Version);
            _workspace.Hub.SetInitialVersion(ownNode.Version);
        }

        var allNodes = new List<MeshNode>();
        if (ownNode != null && !string.IsNullOrEmpty(ownNode.Path))
            allNodes.Add(ownNode);

        _lastSaved = new InstanceCollection(allNodes, node => ((MeshNode)node).Path);
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

    private sealed class FlushOnDispose(MeshNodeTypeSource source) : IDisposable
    {
        public void Dispose()
        {
            lock (source._timerLock)
            {
                source._debounceTimer?.Dispose();
                source._debounceTimer = null;
            }
            source.FlushPendingSaves();
        }
    }
}
