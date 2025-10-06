using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Request to get content collection configuration for an address
/// </summary>
public record GetContentCollectionRequest : IRequest<GetContentCollectionResponse>;

/// <summary>
/// Response containing content collection configuration
/// </summary>
public record GetContentCollectionResponse
{
    /// <summary>
    /// The type of provider (e.g., "FileSystem", "AzureBlob", "EmbeddedResource")
    /// </summary>
    public string? ProviderType { get; init; }

    /// <summary>
    /// The collection name
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// Provider-specific configuration
    /// </summary>
    public Dictionary<string, string>? Configuration { get; init; }

    /// <summary>
    /// Indicates whether a collection was found for this address
    /// </summary>
    public bool IsFound => ProviderType != null;
}
