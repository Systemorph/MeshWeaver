using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// Configuration for a vertex type within a namespace.
/// Defines satellite types and optional hub configuration.
/// </summary>
public sealed record VertexTypeConfig(
    string Name,
    string DisplayName
)
{
    /// <summary>
    /// Satellite types that can be attached to vertices of this type.
    /// </summary>
    public ImmutableList<string> SatelliteTypes { get; init; } = [];

    public string? Description { get; init; }
    public string? IconName { get; init; }
}
