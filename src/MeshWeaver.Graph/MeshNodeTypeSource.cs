using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for MeshNode that syncs to IPersistenceService.
/// Loads own node + children on init, syncs adds/updates/deletes to persistence.
/// Also propagates Content changes to the content data source.
/// </summary>
public record MeshNodeTypeSource : TypeSourceWithType<MeshNode, MeshNodeTypeSource>
{
    private readonly IPersistenceService _persistence;
    private readonly string _hubPath;  // e.g., "graph/org1"
    private readonly IWorkspace _workspace;
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    public MeshNodeTypeSource(IWorkspace workspace, object dataSource, IPersistenceService persistence, string hubPath)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistence = persistence;
        _hubPath = hubPath;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshNodeTypeSource>>();
        _logger?.LogWarning("MeshNodeTypeSource: Created for hubPath={HubPath}", hubPath);
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogWarning("MeshNodeTypeSource.UpdateImpl: Called with {Count} instances, _lastSaved has {LastSavedCount}",
            instances.Instances.Count, _lastSaved.Instances.Count);

        // Detect adds (new nodes)
        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        // Detect updates
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && !existing.Equals(x.Value))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        // Detect deletes
        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        _logger?.LogWarning("MeshNodeTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        // Sync to persistence
        // IPersistenceService handles partition automatically based on node.Prefix:
        // - Own node (Prefix == _hubPath) → saved to parent partition (file: parentPath/nodeName.json)
        // - Child nodes (Prefix starts with _hubPath/) → saved to own partition (file: _hubPath/childName.json)
        foreach (var node in adds.Concat(updates))
        {
            _logger?.LogWarning("MeshNodeTypeSource.UpdateImpl: Saving node {Prefix} to persistence, Content={ContentType}",
                node.Prefix, node.Content?.GetType().Name ?? "null");
            _ = _persistence.SaveNodeAsync(node);
        }

        foreach (var node in deletes)
            _ = _persistence.DeleteNodeAsync(node.Prefix, recursive: true);

        // Propagate Content changes to the content data source
        PropagateContentChanges(updates);

        _lastSaved = instances;
        return instances;
    }

    private void PropagateContentChanges(MeshNode[] updates)
    {
        foreach (var node in updates)
        {
            if (node.Prefix != _hubPath || node.Content == null)
                continue;

            // Get the previous node to check if content changed
            if (!_lastSaved.Instances.TryGetValue(node.Key, out var previousObj))
                continue;

            var previousNode = (MeshNode)previousObj;
            if (previousNode.Content?.Equals(node.Content) == true)
                continue;

            // Content has changed, update the content data source
            var contentType = node.Content.GetType();
            try
            {
                // Check if there's a data source for this content type
                var dataContext = _workspace.DataContext;
                if (!dataContext.DataSourcesByCollection.ContainsKey(contentType.Name))
                    continue;

                // Update the content data source
                _workspace.RequestChange(
                    DataChangeRequest.Update([node.Content]),
                    null,
                    null
                );
            }
            catch
            {
                // Content type not registered, ignore
            }
        }
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        // Load own MeshNode doc (stored in parent's partition)
        // File location: parentPath/ownNodeName.json (e.g., "graph/org1.json")
        var ownNode = await _persistence.GetNodeAsync(_hubPath, ct);

        // Load children from own partition
        // File location: _hubPath/*.json (e.g., "graph/org1/*.json")
        var allNodes = new List<MeshNode>();
        if (ownNode != null) allNodes.Add(ownNode);
        await foreach (var child in _persistence.GetChildrenAsync(_hubPath).WithCancellation(ct))
            allNodes.Add(child);

        _lastSaved = new InstanceCollection(allNodes, TypeDefinition.GetKey);
        return _lastSaved;
    }
}
