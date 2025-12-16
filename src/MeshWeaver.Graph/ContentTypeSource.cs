using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for content entities (e.g., Story, Article) that syncs with MeshNode.Content.
/// - On initialization: Loads content from MeshNode.Content
/// - On update: Syncs changes back to MeshNode.Content (which triggers persistence via MeshNodeTypeSource)
/// </summary>
public record ContentTypeSource<T> : TypeSourceWithType<T, ContentTypeSource<T>> where T : class
{
    private readonly IPersistenceService _persistence;
    private readonly string _hubPath;
    private readonly IWorkspace _workspace;
    private InstanceCollection _lastSaved = new();

    public ContentTypeSource(IWorkspace workspace, object dataSource, IPersistenceService persistence, string hubPath)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistence = persistence;
        _hubPath = hubPath;

        // Auto-configure key function from [Key] attribute
        var keyProperty = typeof(T).GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() != null);

        if (keyProperty != null)
        {
            TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
                TypeDefinition.CollectionName,
                new KeyFunction(o => keyProperty.GetValue(o)!, keyProperty.PropertyType));
        }
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        // Detect changes: adds, updates, deletes
        var hasChanges = !_lastSaved.Instances.SequenceEqual(instances.Instances);

        if (hasChanges && instances.Instances.Count > 0)
        {
            // Get the content entity (there should be exactly one for a node's content)
            var contentEntity = instances.Instances.Values.FirstOrDefault();

            if (contentEntity != null)
            {
                // Sync content back to MeshNode.Content
                SyncToMeshNode(contentEntity);
            }
        }

        _lastSaved = instances;
        return instances;
    }

    private void SyncToMeshNode(object contentEntity)
    {
        // Get current MeshNode from the workspace's MeshNode stream
        var meshNodeStream = _workspace.GetStream(typeof(MeshNode));
        var currentStore = meshNodeStream.Current?.Value;

        if (currentStore == null) return;

        var meshNodeCollection = currentStore.Reduce(new CollectionReference(typeof(MeshNode).Name));
        var meshNode = meshNodeCollection.Instances.Values
            .Cast<MeshNode>()
            .FirstOrDefault(n => n.Prefix == _hubPath);

        if (meshNode == null) return;

        // Check if content actually changed
        if (meshNode.Content?.Equals(contentEntity) == true) return;

        // Update MeshNode with new content
        var updatedNode = meshNode with { Content = contentEntity };

        // Post update to MeshNode stream (MeshNodeTypeSource will handle persistence)
        _workspace.RequestChange(
            DataChangeRequest.Update([updatedNode]),
            null, // no activity tracking needed for internal sync
            null  // no request to respond to
        );
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        // Load the MeshNode from persistence to get the content
        var meshNode = await _persistence.GetNodeAsync(_hubPath, ct);

        if (meshNode?.Content is T content)
        {
            // Return the content as the initial data
            _lastSaved = new InstanceCollection([content], TypeDefinition.GetKey);
            return _lastSaved;
        }

        // No content found, return empty collection
        _lastSaved = new InstanceCollection();
        return _lastSaved;
    }
}
