using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// IContentInitializable is in MeshWeaver.Mesh namespace

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
        _logger?.LogDebug("ContentTypeSource<{Type}>: Created for hubPath={HubPath}", typeof(T).Name, hubPath);

        // Auto-configure key function from [Key] attribute
        var keyProperty = typeof(T).GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() != null);

        if (keyProperty != null)
        {
            TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
                TypeDefinition.CollectionName,
                new KeyFunction(o => keyProperty.GetValue(o)!, keyProperty.PropertyType));
            _logger?.LogDebug("ContentTypeSource<{Type}>: Configured key from property {KeyProperty}", typeof(T).Name, keyProperty.Name);
        }
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogDebug("ContentTypeSource<{Type}>.UpdateImpl: Called with {Count} instances, _lastSaved has {LastSavedCount}",
            typeof(T).Name, instances.Instances.Count, _lastSaved.Instances.Count);

        // Detect changes: adds, updates, deletes
        var hasChanges = !_lastSaved.Instances.SequenceEqual(instances.Instances);
        _logger?.LogDebug("ContentTypeSource<{Type}>.UpdateImpl: hasChanges={HasChanges}", typeof(T).Name, hasChanges);

        if (hasChanges && instances.Instances.Count > 0)
        {
            // Get the content entity (there should be exactly one for a node's content)
            var contentEntity = instances.Instances.Values.FirstOrDefault();
            _logger?.LogDebug("ContentTypeSource<{Type}>.UpdateImpl: contentEntity={ContentEntity}", typeof(T).Name, contentEntity);

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
        _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Called for hubPath={HubPath}", typeof(T).Name, _hubPath);

        // Read current MeshNode directly from persistence and update it synchronously
        // Using GetAwaiter().GetResult() to avoid async issues crossing test boundaries
        try
        {
            var meshNode = _persistence.GetNodeAsync(_hubPath).GetAwaiter().GetResult();

            if (meshNode == null)
            {
                _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: No MeshNode found at path {HubPath}", typeof(T).Name, _hubPath);
                return;
            }

            _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Found MeshNode Prefix={Prefix}, Content type={ContentType}",
                typeof(T).Name, meshNode.Path, meshNode.Content?.GetType().Name ?? "null");

            // Check if content actually changed
            if (meshNode.Content?.Equals(contentEntity) == true)
            {
                _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Content is already equal, skipping update", typeof(T).Name);
                return;
            }

            // Update MeshNode with new content and current hub version
            var hubVersion = _workspace.Hub.Version;
            var (extractedName, extractedDescription, extractedIcon, extractedCategory) = ExtractMeshNodeProperties(contentEntity);
            var updatedNode = meshNode with
            {
                Content = contentEntity,
                Version = hubVersion,
                Name = extractedName ?? meshNode.Name,
                Description = extractedDescription ?? meshNode.Description,
                Icon = extractedIcon ?? meshNode.Icon,
                Category = extractedCategory ?? meshNode.Category
            };

            // Save to persistence synchronously
            _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Saving MeshNode to persistence with new content, Version={Version}", typeof(T).Name, hubVersion);
            _persistence.SaveNodeAsync(updatedNode).GetAwaiter().GetResult();
            _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Persistence save complete", typeof(T).Name);

            // Also update the in-memory MeshNode data stream via RequestChange
            // This ensures GetDataRequest for MeshNode returns the updated content
            _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Posting MeshNode update to data stream", typeof(T).Name);
            _workspace.RequestChange(
                DataChangeRequest.Update([updatedNode]),
                null,
                null
            );
            _logger?.LogDebug("ContentTypeSource<{Type}>.SyncToMeshNode: Data stream update posted", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ContentTypeSource<{Type}>.SyncToMeshNode: Error syncing to MeshNode", typeof(T).Name);
        }
    }

    /// <summary>
    /// Extracts MeshNode properties from content entity using attribute-based and convention-based mapping.
    /// Priority for Name: [MeshNodeProperty("Name")] > INamed.DisplayName > Title property > Name property
    /// Priority for Description: [MeshNodeProperty("Description")] > Description property
    /// Priority for Icon: [MeshNodeProperty("Icon")] > Icon property
    /// Priority for Category: [MeshNodeProperty("Category")] > Category property
    /// </summary>
    private static (string? Name, string? Description, string? Icon, string? Category) ExtractMeshNodeProperties(object content)
    {
        string? name = null;
        string? description = null;
        string? icon = null;
        string? category = null;
        var type = content.GetType();

        // Priority 1: Look for [MeshNodeProperty] attributes
        foreach (var prop in type.GetProperties())
        {
            var attr = prop.GetCustomAttribute<MeshNodePropertyAttribute>();
            if (attr == null || prop.PropertyType != typeof(string))
                continue;

            var value = prop.GetValue(content) as string;
            switch (attr.MeshNodeProperty)
            {
                case "Name" when name == null:
                    name = value;
                    break;
                case "Description" when description == null:
                    description = value;
                    break;
                case "Icon" when icon == null:
                    icon = value;
                    break;
                case "Category" when category == null:
                    category = value;
                    break;
            }
        }

        // Priority 2: INamed interface for DisplayName
        if (name == null && content is INamed named)
        {
            name = named.DisplayName;
        }

        // Priority 3: Convention - look for Title property
        if (name == null)
        {
            var titleProp = type.GetProperty("Title");
            if (titleProp?.PropertyType == typeof(string))
                name = titleProp.GetValue(content) as string;
        }

        // Priority 4: Convention - look for Name property
        if (name == null)
        {
            var nameProp = type.GetProperty("Name");
            if (nameProp?.PropertyType == typeof(string))
                name = nameProp.GetValue(content) as string;
        }

        // Convention - look for Description property (if not already set via attribute)
        if (description == null)
        {
            var descProp = type.GetProperty("Description");
            if (descProp?.PropertyType == typeof(string))
                description = descProp.GetValue(content) as string;
        }

        // Convention - look for Icon property (if not already set via attribute)
        if (icon == null)
        {
            var iconProp = type.GetProperty("Icon");
            if (iconProp?.PropertyType == typeof(string))
                icon = iconProp.GetValue(content) as string;
        }

        // Convention - look for Category property (if not already set via attribute)
        if (category == null)
        {
            var catProp = type.GetProperty("Category");
            if (catProp?.PropertyType == typeof(string))
                category = catProp.GetValue(content) as string;
        }

        return (name, description, icon, category);
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: Loading MeshNode from hubPath={HubPath}", typeof(T).Name, _hubPath);

        // Load the MeshNode from persistence to get the content
        var meshNode = await _persistence.GetNodeAsync(_hubPath, ct);
        _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: meshNode={MeshNode}, Content type={ContentType}",
            typeof(T).Name, meshNode?.Path ?? "null", meshNode?.Content?.GetType().Name ?? "null");

        T? content = null;

        if (meshNode?.Content is T typedContent)
        {
            content = typedContent;
        }
        else if (meshNode?.Content is JsonElement jsonElement)
        {
            // Deserialize JsonElement to expected type T using Hub's options
            try
            {
                content = jsonElement.Deserialize<T>(_workspace.Hub.JsonSerializerOptions);
                _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: Deserialized JsonElement to {Type}", typeof(T).Name, typeof(T).Name);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "ContentTypeSource<{Type}>.InitializeAsync: Failed to deserialize JsonElement", typeof(T).Name);
            }
        }

        if (content != null)
        {
            _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: Found content, returning it", typeof(T).Name);

            // Call Initialize() if content implements IContentInitializable
            if (content is IContentInitializable initializable)
            {
                var initialized = initializable.Initialize();
                if (initialized is T typedInitialized)
                {
                    content = typedInitialized;
                    _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: Content was initialized via IContentInitializable", typeof(T).Name);
                }
            }

            // Return the content as the initial data
            _lastSaved = new InstanceCollection([content], TypeDefinition.GetKey);
            return _lastSaved;
        }

        _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: No content found, returning empty", typeof(T).Name);
        // No content found, return empty collection
        _lastSaved = new InstanceCollection();
        return _lastSaved;
    }
}
