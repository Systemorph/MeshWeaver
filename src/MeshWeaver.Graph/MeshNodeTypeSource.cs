using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for MeshNode that syncs to IPersistenceService.
/// Loads own node + children on init, syncs adds/updates/deletes to persistence.
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
        _logger?.LogDebug("MeshNodeTypeSource: Created for hubPath={HubPath}", hubPath);

        // Register key function for MeshNode using Path as the key
        TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
            TypeDefinition.CollectionName,
            new KeyFunction(o => ((MeshNode)o).Path, typeof(string)));
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        // Merge partial updates with existing nodes
        // This handles the case where auto-save sends only Content updates
        instances = MergePartialUpdates(instances);

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

        _logger?.LogDebug("MeshNodeTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        // Sync to persistence
        // IPersistenceService handles partition automatically based on node.Path:
        // - Own node (Namespace == _hubPath) → saved to parent partition (file: parentPath/nodeName.json)
        // - Child nodes (Namespace starts with _hubPath/) → saved to own partition (file: _hubPath/childName.json)
        var hubVersion = _workspace.Hub.Version;
        foreach (var node in adds.Concat(updates))
        {
            // Capture hub version when saving
            var nodeWithVersion = node with { Version = hubVersion };

            // Save synchronously to ensure it completes before returning
            // TODO: Consider making this async with proper task tracking
            try
            {
                _persistence.SaveNodeAsync(nodeWithVersion).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MeshNodeTypeSource: Save failed for {Path}", nodeWithVersion.Path);
            }
        }

        foreach (var node in deletes)
            _ = _persistence.DeleteNodeAsync(node.Path, recursive: true);

        _lastSaved = instances;
        return instances;
    }

    /// <summary>
    /// Merges partial MeshNode updates with existing nodes.
    /// When auto-save sends only Content updates, this preserves other fields like Name, Description, etc.
    /// </summary>
    private InstanceCollection MergePartialUpdates(InstanceCollection instances)
    {
        var mergedInstances = new Dictionary<object, object>(instances.Instances);
        var anyMerged = false;

        foreach (var kvp in instances.Instances)
        {
            if (kvp.Value is not MeshNode incomingNode)
                continue;

            // Check if we have an existing node to merge with
            if (!_lastSaved.Instances.TryGetValue(kvp.Key, out var existingObj) ||
                existingObj is not MeshNode existingNode)
                continue;

            // Check if this looks like a partial update (only Content and NodeType are set)
            // A partial update typically has Content but is missing other metadata fields
            var isPartialUpdate = incomingNode.Content != null &&
                                  string.IsNullOrEmpty(incomingNode.Name) &&
                                  string.IsNullOrEmpty(incomingNode.Category) &&
                                  string.IsNullOrEmpty(incomingNode.Icon);

            if (!isPartialUpdate)
                continue;

            // Merge: preserve existing fields, update Content and NodeType
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
        // Load own MeshNode doc only (stored in parent's partition)
        // Note: Children are NOT loaded here - they are accessed via their own hubs
        var ownNode = await _persistence.GetNodeAsync(_hubPath, ct);

        // Resolve JsonElement content to proper types.
        // This handles cases where the $type discriminator in the database doesn't match
        // the registered short name (e.g., "MeshWeaver.Markdown.MarkdownContent" vs "MarkdownContent").
        if (ownNode != null)
            ownNode = ResolveJsonElementContent(ownNode);

        // Restore hub version from persisted MeshNode
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

    /// <summary>
    /// Resolves JsonElement content to the proper CLR type by looking up the $type discriminator
    /// in the hub's TypeRegistry. Handles both short names (e.g., "MarkdownContent") and
    /// full namespace-qualified names (e.g., "MeshWeaver.Markdown.MarkdownContent").
    /// </summary>
    private MeshNode ResolveJsonElementContent(MeshNode node)
    {
        if (node.Content is not JsonElement je || je.ValueKind != JsonValueKind.Object)
            return node;

        // Try to extract $type discriminator
        if (!je.TryGetProperty("$type", out var typeProp))
            return node;

        var typeName = typeProp.GetString();
        if (string.IsNullOrEmpty(typeName))
            return node;

        var registry = _workspace.Hub.TypeRegistry;

        // Try the full type name as-is
        if (!registry.TryGetType(typeName, out var typeDef) || typeDef?.Type == null)
        {
            // Try the short name (last segment after '.')
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
