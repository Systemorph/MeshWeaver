namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a node type that can be created as a child of the current node.
/// Contains display information for rendering type selection UI.
/// </summary>
/// <param name="NodeTypePath">The full path to the NodeType (e.g., "ACME/Project/Todo")</param>
/// <param name="DisplayName">Optional display name for the type in UI</param>
/// <param name="Icon">Optional icon URL or identifier for UI</param>
/// <param name="Description">Optional description of this node type</param>
/// <param name="Order">Display order for sorting in UI lists</param>
/// <param name="ContentType">The content type for this node type, used for collecting properties in create dialog</param>
/// <param name="SubNamespace">Optional sub-namespace folder for created nodes (e.g., "Threads" creates at parent/Threads/id). Defaults to last segment of NodeTypePath.</param>
public record CreatableTypeInfo(
    string NodeTypePath,
    string? DisplayName = null,
    string? Icon = null,
    string? Description = null,
    int Order = 0,
    Type? ContentType = null,
    string? SubNamespace = null)
{
    /// <summary>
    /// Whether the type's definition declares <c>ownsPartition</c> — an instance of it is a partition
    /// root, so its path is just its id and the create form must place it at the top level.
    ///
    /// <para>Carried here because the provider already materialises each offered type's definition
    /// to build this record, so the form learns it at no extra read — including for a type declared
    /// in mesh content, which the static registry cannot see (#4449). 🚨 A UI convenience only: the
    /// boundary refusal of a nested instance is <c>OwnsPartitionProvisioningValidator</c>, which a
    /// caller posting a create directly meets whatever this says.</para>
    ///
    /// <para>An <c>init</c> property rather than a positional parameter, so the record's primary
    /// constructor — and every caller binding it — is unchanged.</para>
    /// </summary>
    public bool OwnsPartition { get; init; }
}

/// <summary>
/// Snapshot of creatable types emitted by the observable.
/// <see cref="IsLoading"/> is true while more items may still arrive.
/// </summary>
public record CreatableTypesSnapshot(IReadOnlyList<CreatableTypeInfo> Items, bool IsLoading)
{
    /// <summary>Empty snapshot with no items and not loading.</summary>
    public static readonly CreatableTypesSnapshot Empty = new([], false);
    /// <summary>Creates a snapshot indicating more items may still arrive.</summary>
    public static CreatableTypesSnapshot Loading(IReadOnlyList<CreatableTypeInfo> items) => new(items, true);
    /// <summary>Creates a final snapshot with all items loaded.</summary>
    public static CreatableTypesSnapshot Done(IReadOnlyList<CreatableTypeInfo> items) => new(items, false);
}
