using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for MeshNode that syncs to IPersistenceService.
/// Loads own node + children on init, syncs adds/updates/deletes to persistence.
/// </summary>
public record MeshNodeTypeSource : TypeSourceWithType<MeshNode, MeshNodeTypeSource>
{
    private readonly IPersistenceService _persistence;
    private readonly string _hubPath;  // e.g., "graph/org1"
    private InstanceCollection _lastSaved = new();

    public MeshNodeTypeSource(IWorkspace workspace, object dataSource, IPersistenceService persistence, string hubPath)
        : base(workspace, dataSource)
    {
        _persistence = persistence;
        _hubPath = hubPath;
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
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

        // Sync to persistence
        // IPersistenceService handles partition automatically based on node.Prefix:
        // - Own node (Prefix == _hubPath) → saved to parent partition (file: parentPath/nodeName.json)
        // - Child nodes (Prefix starts with _hubPath/) → saved to own partition (file: _hubPath/childName.json)
        foreach (var node in adds.Concat(updates))
            _ = _persistence.SaveNodeAsync(node);

        foreach (var node in deletes)
            _ = _persistence.DeleteNodeAsync(node.Prefix, recursive: true);

        _lastSaved = instances;
        return instances;
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
        var children = await _persistence.GetChildrenAsync(_hubPath, ct);

        var allNodes = new List<MeshNode>();
        if (ownNode != null) allNodes.Add(ownNode);
        allNodes.AddRange(children);

        _lastSaved = new InstanceCollection(allNodes, TypeDefinition.GetKey);
        return _lastSaved;
    }
}
