using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// Core graph entity representing a node in the information network.
/// Addressed as @org/namespace/type/id (e.g., @loot/acme/marketing/story/12345)
/// </summary>
public sealed record Vertex(
    Guid Id,
    string Organization,
    string Namespace,
    string Type,
    string Name
)
{
    public string? Text { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// List of vertex references this vertex depends on.
    /// Format: "org/namespace/type/id"
    /// </summary>
    public ImmutableList<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Returns the unified reference path for this vertex.
    /// </summary>
    public string ToPath() => $"{Organization}/{Namespace}/{Type}/{Id}";
}

/// <summary>
/// Comment attached to a vertex.
/// </summary>
public sealed record VertexComment(
    Guid Id,
    Guid VertexId,
    string Author,
    string Text
)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ModifiedAt { get; init; }
}
