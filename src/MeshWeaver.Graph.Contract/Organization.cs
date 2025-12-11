using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// Top-level organization that groups MeshNamespaces.
/// Addressed as @loot/{orgName} (e.g., @loot/acme)
/// </summary>
public sealed record Organization(
    string Name,
    string DisplayName
)
{
    public string? Description { get; init; }
    public string? IconName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Namespaces belonging to this organization.
    /// </summary>
    public ImmutableList<GraphNamespace> Namespaces { get; init; } = [];
}
