namespace MeshWeaver.Mesh;

/// <summary>
/// Content type for Partition nodes that define storage partition metadata.
/// Partition nodes live at Admin/Partition/{Name}.
/// </summary>
public record PartitionDefinition
{
    /// <summary>
    /// The base path namespaces this partition serves.
    /// Example: A "Documentation" partition serves ["Doc"].
    /// </summary>
    public IReadOnlyCollection<string> BasePaths { get; init; } = new HashSet<string>();

    /// <summary>
    /// Storage backend type: "FileSystem", "PostgreSql", "Static", "Auto".
    /// </summary>
    public string? StorageType { get; init; }

    /// <summary>
    /// Human-readable description of this partition.
    /// </summary>
    public string? Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
