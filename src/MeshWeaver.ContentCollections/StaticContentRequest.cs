using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Request for static content from a hub using addressType/addressId pattern
/// </summary>
public record GetStaticContentRequest(
    string Path
) : IRequest<GetStaticContentResponse>;

/// <summary>
/// Response containing static content with source type indication
/// </summary>
public record GetStaticContentResponse(
    string? ContentType,
    string? FileName
)
{
    /// <summary>
    /// Source type: "Inline" for inline content, or provider type (e.g., "FileSystem", "AzureBlob")
    /// </summary>
    public string? SourceType { get; init; }

    /// <summary>
    /// Logical name of the stream provider (from configuration)
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Direct content for small text-based files when SourceType is "Inline"
    /// </summary>
    public string? InlineContent { get; init; }

    /// <summary>
    /// Provider-specific reference (e.g., file path, blob URL) when SourceType is not "Inline"
    /// </summary>
    public string? ProviderReference { get; init; }

    /// <summary>
    /// Indicates whether the content was found
    /// </summary>
    public bool IsFound => SourceType != null;

    public const string InlineSourceType = "Inline";
};
