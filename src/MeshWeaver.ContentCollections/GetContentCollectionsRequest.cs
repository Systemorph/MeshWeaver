using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Request to get content collection configurations by name
/// </summary>
public record GetContentCollectionRequest(params IReadOnlyCollection<string>? CollectionNames) : IRequest<GetContentCollectionResponse>;

/// <summary>
/// Response containing content collection configurations
/// </summary>
public record GetContentCollectionResponse
{
    public IReadOnlyCollection<ContentCollectionConfig> Collections { get; init; } = [];
}
