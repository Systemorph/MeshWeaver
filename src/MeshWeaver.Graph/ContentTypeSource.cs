using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger? _logger;
    private InstanceCollection _lastSaved = new();

    public ContentTypeSource(IWorkspace workspace, object dataSource, IPersistenceService persistence, string hubPath)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistence = persistence;
        _hubPath = hubPath;
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<ContentTypeSource<T>>>();
        _logger?.LogWarning("ContentTypeSource<{Type}>: Created for hubPath={HubPath}", typeof(T).Name, hubPath);

        // Auto-configure key function from [Key] attribute
        var keyProperty = typeof(T).GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() != null);

        if (keyProperty != null)
        {
            TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
                TypeDefinition.CollectionName,
                new KeyFunction(o => keyProperty.GetValue(o)!, keyProperty.PropertyType));
            _logger?.LogWarning("ContentTypeSource<{Type}>: Configured key from property {KeyProperty}", typeof(T).Name, keyProperty.Name);
        }
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogWarning("ContentTypeSource<{Type}>.UpdateImpl: Called with {Count} instances, _lastSaved has {LastSavedCount}",
            typeof(T).Name, instances.Instances.Count, _lastSaved.Instances.Count);

        // Detect changes: adds, updates, deletes
        var hasChanges = !_lastSaved.Instances.SequenceEqual(instances.Instances);
        _logger?.LogWarning("ContentTypeSource<{Type}>.UpdateImpl: hasChanges={HasChanges}", typeof(T).Name, hasChanges);

        if (hasChanges && instances.Instances.Count > 0)
        {
            // Get the content entity (there should be exactly one for a node's content)
            var contentEntity = instances.Instances.Values.FirstOrDefault();
            _logger?.LogWarning("ContentTypeSource<{Type}>.UpdateImpl: contentEntity={ContentEntity}", typeof(T).Name, contentEntity);

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
        _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Called for hubPath={HubPath}", typeof(T).Name, _hubPath);

        // Read current MeshNode directly from persistence and update it synchronously
        // Using GetAwaiter().GetResult() to avoid async issues crossing test boundaries
        try
        {
            var meshNode = _persistence.GetNodeAsync(_hubPath).GetAwaiter().GetResult();

            if (meshNode == null)
            {
                _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: No MeshNode found at path {HubPath}", typeof(T).Name, _hubPath);
                return;
            }

            _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Found MeshNode Prefix={Prefix}, Content type={ContentType}",
                typeof(T).Name, meshNode.Prefix, meshNode.Content?.GetType().Name ?? "null");

            // Check if content actually changed
            if (meshNode.Content?.Equals(contentEntity) == true)
            {
                _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Content is already equal, skipping update", typeof(T).Name);
                return;
            }

            // Update MeshNode with new content and current hub version
            var hubVersion = _workspace.Hub.Version;
            var updatedNode = meshNode with { Content = contentEntity, Version = hubVersion };

            // Save to persistence synchronously
            _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Saving MeshNode to persistence with new content, Version={Version}", typeof(T).Name, hubVersion);
            _persistence.SaveNodeAsync(updatedNode).GetAwaiter().GetResult();
            _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Persistence save complete", typeof(T).Name);

            // Also update the in-memory MeshNode data stream via RequestChange
            // This ensures GetDataRequest for MeshNode returns the updated content
            _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Posting MeshNode update to data stream", typeof(T).Name);
            _workspace.RequestChange(
                DataChangeRequest.Update([updatedNode]),
                null,
                null
            );
            _logger?.LogWarning("ContentTypeSource<{Type}>.SyncToMeshNode: Data stream update posted", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ContentTypeSource<{Type}>.SyncToMeshNode: Error syncing to MeshNode", typeof(T).Name);
        }
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        _logger?.LogWarning("ContentTypeSource<{Type}>.InitializeAsync: Loading MeshNode from hubPath={HubPath}", typeof(T).Name, _hubPath);

        // Load the MeshNode from persistence to get the content
        var meshNode = await _persistence.GetNodeAsync(_hubPath, ct);
        _logger?.LogWarning("ContentTypeSource<{Type}>.InitializeAsync: meshNode={MeshNode}, Content type={ContentType}",
            typeof(T).Name, meshNode?.Prefix ?? "null", meshNode?.Content?.GetType().Name ?? "null");

        if (meshNode?.Content is T content)
        {
            _logger?.LogWarning("ContentTypeSource<{Type}>.InitializeAsync: Found content, returning it", typeof(T).Name);
            // Return the content as the initial data
            _lastSaved = new InstanceCollection([content], TypeDefinition.GetKey);
            return _lastSaved;
        }

        _logger?.LogWarning("ContentTypeSource<{Type}>.InitializeAsync: No content found, returning empty", typeof(T).Name);
        // No content found, return empty collection
        _lastSaved = new InstanceCollection();
        return _lastSaved;
    }
}
