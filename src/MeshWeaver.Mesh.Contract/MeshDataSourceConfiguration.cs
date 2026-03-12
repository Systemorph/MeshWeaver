using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh;

/// <summary>
/// Content record for MeshDataSource nodes.
/// Stored as MeshNode.Content for nodes with NodeType = "MeshDataSource".
/// </summary>
public record MeshDataSourceConfiguration
{
    /// <summary>
    /// Provider type: FileSystem, Postgres, Cosmos, AzureBlob, Agents, Documentation
    /// </summary>
    public string ProviderType { get; init; } = "FileSystem";

    /// <summary>
    /// Whether this source is enabled (serves data to queries).
    /// When disabled via Install, shows a note explaining the source is installed elsewhere.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether nodes from this source appear in search results.
    /// </summary>
    public bool IncludeInSearch { get; init; } = true;

    /// <summary>
    /// The underlying storage configuration for this source.
    /// Reuses GraphStorageConfig which supports FileSystem, AzureBlob, Cosmos, PostgreSql.
    /// </summary>
    public GraphStorageConfig? StorageConfig { get; init; }

    /// <summary>
    /// If this source was installed (copied with sync) into another source,
    /// records the destination path/source for display purposes.
    /// </summary>
    public string? InstalledTo { get; init; }

    /// <summary>
    /// Timestamp of last install/sync operation.
    /// </summary>
    public DateTimeOffset? LastSyncedAt { get; init; }

    /// <summary>
    /// Human-readable description of this data source.
    /// </summary>
    public string? Description { get; init; }
}
