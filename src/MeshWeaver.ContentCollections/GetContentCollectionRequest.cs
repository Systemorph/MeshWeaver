using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Request to get content collection configuration for an address
/// </summary>
public record GetContentCollectionRequest : IRequest<GetContentCollectionResponse>;

/// <summary>
/// Response containing content collection configurations
/// </summary>
public record GetContentCollectionResponse
{
    /// <summary>
    /// The collection of content collection configurations
    /// </summary>
    public IReadOnlyCollection<ContentCollectionConfig>? Collections { get; init; }

    /// <summary>
    /// Indicates whether any collections were found for this address
    /// </summary>
    public bool IsFound => Collections?.Count > 0;
}
