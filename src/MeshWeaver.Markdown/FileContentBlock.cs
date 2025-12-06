using Markdig.Parsers;
using Markdig.Syntax;
using MeshWeaver.Data;

namespace MeshWeaver.Markdown;

/// <summary>
/// Block representing a file content reference: @("content:addressType/addressId/collection/path")
/// Renders based on mime type with fallback to download link.
/// </summary>
public class FileContentBlock : ContainerBlock
{
    public FileContentBlock(FileContentReference reference, BlockParser parser) : base(parser)
    {
        FileReference = reference ?? throw new ArgumentNullException(nameof(reference));
    }

    /// <summary>
    /// The parsed file content reference.
    /// </summary>
    public FileContentReference FileReference { get; }

    /// <summary>
    /// The address string for routing (addressType/addressId).
    /// </summary>
    public string Address => $"{FileReference.AddressType}/{FileReference.AddressId}";

    /// <summary>
    /// The full path for the unified reference.
    /// </summary>
    public string Path => FileReference.ToPath();
}
