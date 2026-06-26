#nullable enable
namespace MeshWeaver.ContentCollections;

/// <summary>
/// Metadata describing an author of content (e.g. a markdown article), typically loaded
/// from an <c>authors.json</c> file stored in a content collection.
/// </summary>
/// <param name="FirstName">The author's first (given) name.</param>
/// <param name="LastName">The author's last (family) name.</param>
public record Author(string FirstName, string LastName)
{
    /// <summary>Optional middle name; <c>null</c> when not specified.</summary>
    public string? MiddleName { get; init; }
    /// <summary>Optional URL of the author's avatar or portrait image; <c>null</c> when not specified.</summary>
    public string? ImageUrl { get; init; }
}
