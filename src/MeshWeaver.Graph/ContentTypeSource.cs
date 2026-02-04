using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for content types that provides a virtual collection allowing direct
/// DataChangeRequest operations on content without manually wrapping in MeshNode.
///
/// On create/update: Wraps content in the hub's MeshNode and updates it.
/// On read: Extracts content from MeshNode.Content.
///
/// This enables two update paths:
/// 1. Update MeshNode directly (includes Content) - existing behavior
/// 2. Update just the content type (auto-wraps in MeshNode) - via this source
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

        // Register key function for content type
        // Use Path as the key to match MeshNode semantics
        TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
            TypeDefinition.CollectionName,
            new KeyFunction(GetContentKey, typeof(string)));
    }

    /// <summary>
    /// Gets the key from a content instance.
    /// Uses [Key] attribute if present, otherwise falls back to hubPath.
    /// </summary>
    private object GetContentKey(object content)
    {
        // Try to get key from [Key] attribute
        var keyProperty = typeof(T).GetProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<System.ComponentModel.DataAnnotations.KeyAttribute>() != null);

        if (keyProperty != null)
        {
            var keyValue = keyProperty.GetValue(content);
            if (keyValue != null)
                return keyValue;
        }

        // Fall back to hubPath as the key
        return _hubPath;
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        _logger?.LogDebug("ContentTypeSource<{Type}>.UpdateImpl: Called with {Count} instances",
            typeof(T).Name, instances.Instances.Count);

        // Only process if there are actual changes to content instances
        // Compare with _lastSaved to detect real changes (not just passthrough from MeshNode updates)
        var hasChanges = false;
        foreach (var kvp in instances.Instances)
        {
            if (kvp.Value is not T content)
                continue;

            // Check if this is a new or changed content instance
            if (!_lastSaved.Instances.TryGetValue(kvp.Key, out var existing) ||
                !existing.Equals(content))
            {
                hasChanges = true;
                break;
            }
        }

        // Also check for deletions
        if (!hasChanges)
        {
            foreach (var key in _lastSaved.Instances.Keys)
            {
                if (!instances.Instances.ContainsKey(key))
                {
                    hasChanges = true;
                    break;
                }
            }
        }

        if (!hasChanges)
        {
            _logger?.LogDebug("ContentTypeSource<{Type}>.UpdateImpl: No changes detected, skipping",
                typeof(T).Name);
            return instances;
        }

        // For each changed content instance, wrap it in the hub's MeshNode and save
        foreach (var kvp in instances.Instances)
        {
            if (kvp.Value is not T content)
                continue;

            // Skip if unchanged
            if (_lastSaved.Instances.TryGetValue(kvp.Key, out var existing) &&
                existing.Equals(content))
                continue;

            // Get the current MeshNode from persistence (or create new one)
            var existingNode = _persistence.GetNodeAsync(_hubPath).GetAwaiter().GetResult();

            if (existingNode == null)
            {
                _logger?.LogWarning("ContentTypeSource<{Type}>: No MeshNode found at {HubPath}, creating new one",
                    typeof(T).Name, _hubPath);
                existingNode = MeshNode.FromPath(_hubPath);
            }

            // Update the MeshNode's Content and sync MeshNode properties from content
            var updatedNode = UpdateMeshNodeFromContent(existingNode, content);

            // Save via MeshNode data change request (this will trigger MeshNodeTypeSource)
            _workspace.RequestChange(
                DataChangeRequest.Update([updatedNode]),
                null,
                null
            );

            _logger?.LogDebug("ContentTypeSource<{Type}>: Updated MeshNode at {HubPath} with content",
                typeof(T).Name, _hubPath);
        }

        _lastSaved = instances;
        return instances;
    }

    /// <summary>
    /// Updates a MeshNode with content, syncing properties via [MeshNodeProperty] mappings.
    /// </summary>
    private MeshNode UpdateMeshNodeFromContent(MeshNode node, T content)
    {
        var updatedNode = node with { Content = content };

        // Sync content properties to MeshNode properties via [MeshNodeProperty] attribute
        var mappings = GetMeshNodePropertyMappings();

        foreach (var (meshNodeProp, contentProp) in mappings)
        {
            var value = contentProp.GetValue(content);
            if (value == null)
                continue;

            var stringValue = value.ToString();
            if (string.IsNullOrEmpty(stringValue))
                continue;

            updatedNode = meshNodeProp switch
            {
                "Name" => updatedNode with { Name = stringValue },
                "Description" => updatedNode with { Description = stringValue },
                "Icon" => updatedNode with { Icon = stringValue },
                "Category" => updatedNode with { Category = stringValue },
                _ => updatedNode
            };
        }

        return updatedNode;
    }

    /// <summary>
    /// Gets property mappings from content type to MeshNode via [MeshNodeProperty] attributes.
    /// </summary>
    private Dictionary<string, PropertyInfo> GetMeshNodePropertyMappings()
    {
        var mappings = new Dictionary<string, PropertyInfo>();

        foreach (var prop in typeof(T).GetProperties())
        {
            var attr = prop.GetCustomAttribute<MeshNodePropertyAttribute>();
            if (attr?.MeshNodeProperty != null)
            {
                mappings[attr.MeshNodeProperty] = prop;
            }
        }

        return mappings;
    }

    protected override async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken ct)
    {
        _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: Loading content from {HubPath}",
            typeof(T).Name, _hubPath);

        var items = new List<T>();

        // Load the MeshNode and extract its Content
        var node = await _persistence.GetNodeAsync(_hubPath, ct);

        if (node?.Content != null)
        {
            var content = ExtractContent(node);
            if (content != null)
            {
                items.Add(content);
                _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: Extracted content from {HubPath}",
                    typeof(T).Name, _hubPath);
            }
        }
        else
        {
            _logger?.LogDebug("ContentTypeSource<{Type}>.InitializeAsync: No content found at {HubPath}",
                typeof(T).Name, _hubPath);
        }

        _lastSaved = new InstanceCollection(items.Cast<object>(), GetContentKey);
        return _lastSaved;
    }

    /// <summary>
    /// Extracts typed content from a MeshNode, handling JsonElement deserialization.
    /// </summary>
    private T? ExtractContent(MeshNode node)
    {
        if (node.Content == null)
            return null;

        // If already the correct type, return directly
        if (node.Content is T typed)
            return typed;

        // If JsonElement, deserialize using Hub's JsonSerializerOptions for proper type handling
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(jsonElement.GetRawText(), _workspace.Hub.JsonSerializerOptions);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to deserialize JsonElement content for {Path}", node.Path);
            }
        }

        return null;
    }
}
