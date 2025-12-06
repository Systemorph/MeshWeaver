using Markdig.Parsers;
using Markdig.Syntax;
using MeshWeaver.Data;

namespace MeshWeaver.Markdown;

/// <summary>
/// Block representing a data reference: @("data:addressType/addressId[/collection[/entityId]]")
/// Renders as a div with data attributes for client-side data fetching and JSON display.
/// </summary>
public class DataContentBlock : ContainerBlock
{
    public DataContentBlock(DataContentReference reference, BlockParser parser) : base(parser)
    {
        DataReference = reference ?? throw new ArgumentNullException(nameof(reference));
    }

    /// <summary>
    /// The parsed data content reference.
    /// </summary>
    public DataContentReference DataReference { get; }

    /// <summary>
    /// The address string for routing (addressType/addressId).
    /// </summary>
    public string Address => $"{DataReference.AddressType}/{DataReference.AddressId}";

    /// <summary>
    /// The full path for the unified reference.
    /// </summary>
    public string Path => DataReference.ToPath();
}
