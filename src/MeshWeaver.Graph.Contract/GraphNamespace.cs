using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// Configuration for a graph namespace containing vertex type definitions.
/// </summary>
public sealed record GraphNamespace(
    string Name,
    ImmutableList<VertexTypeConfig> Types
)
{
    public string? Description { get; init; }
    public string? IconName { get; init; }
    public int DisplayOrder { get; init; }
}
