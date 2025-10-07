using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Request to get static content from a content collection
/// </summary>
public record GetContentCollectionsRequest(string Path) : IRequest<GetContentCollectionsResponse>;

/// <summary>
/// Response containing static content
/// </summary>
public record GetContentCollectionsResponse(byte[]? Content, string? ContentType);
