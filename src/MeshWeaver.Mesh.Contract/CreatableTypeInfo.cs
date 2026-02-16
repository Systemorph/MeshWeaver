namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a node type that can be created as a child of the current node.
/// Contains display information for rendering type selection UI.
/// </summary>
/// <param name="NodeTypePath">The full path to the NodeType (e.g., "ACME/Project/Todo")</param>
/// <param name="DisplayName">Optional display name for the type in UI</param>
/// <param name="Icon">Optional icon URL or identifier for UI</param>
/// <param name="Description">Optional description of this node type</param>
/// <param name="DisplayOrder">Display order for sorting in UI lists</param>
/// <param name="ContentType">The content type for this node type, used for collecting properties in create dialog</param>
/// <param name="SubNamespace">Optional sub-namespace folder for created nodes (e.g., "Threads" creates at parent/Threads/id). Defaults to last segment of NodeTypePath.</param>
public record CreatableTypeInfo(
    string NodeTypePath,
    string? DisplayName = null,
    string? Icon = null,
    string? Description = null,
    int DisplayOrder = 0,
    Type? ContentType = null,
    string? SubNamespace = null);

/// <summary>
/// Snapshot of creatable types emitted by the observable.
/// <see cref="IsLoading"/> is true while more items may still arrive.
/// </summary>
public record CreatableTypesSnapshot(IReadOnlyList<CreatableTypeInfo> Items, bool IsLoading)
{
    public static readonly CreatableTypesSnapshot Empty = new([], false);
    public static CreatableTypesSnapshot Loading(IReadOnlyList<CreatableTypeInfo> items) => new(items, true);
    public static CreatableTypesSnapshot Done(IReadOnlyList<CreatableTypeInfo> items) => new(items, false);
}
