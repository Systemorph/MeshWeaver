namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for a content collection (avatars, logos, etc.).
/// Stored in _config/contentCollections/{id}.json
/// </summary>
public record ContentCollectionConfig
{
    /// <summary>
    /// Unique identifier for the collection.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The collection name used in routes and references.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Source type: "FileSystem" or "AzureBlob".
    /// </summary>
    public string SourceType { get; init; } = "FileSystem";

    /// <summary>
    /// Base path for FileSystem, container name for AzureBlob.
    /// </summary>
    public string? BasePath { get; init; }

    /// <summary>
    /// Configuration key to read path from IConfiguration (e.g., "personsPath").
    /// When specified, reads path from Graph:{ConfigurationKey} section.
    /// </summary>
    public string? ConfigurationKey { get; init; }

    /// <summary>
    /// Additional settings specific to the source type.
    /// </summary>
    public Dictionary<string, string>? Settings { get; init; }
}
