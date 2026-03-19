namespace MeshWeaver.Mesh;

/// <summary>
/// Hints resolved from a ParsedQuery by the query routing pipeline.
/// Partition narrows which schema to query (null = fan out to all).
/// Table narrows which table within the schema (null = default mesh_nodes).
/// </summary>
public record QueryRoutingHints
{
    /// <summary>
    /// Target partition (first path segment / schema name). Null = fan out to all partitions.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>
    /// Target table name (e.g., "access", "threads"). Null = default mesh_nodes.
    /// </summary>
    public string? Table { get; init; }
}

/// <summary>
/// A rule that inspects a ParsedQuery and returns routing hints.
/// Return null to abstain (let other rules decide).
/// Rules are applied in order; first non-null Partition/Table wins.
/// </summary>
public delegate QueryRoutingHints? QueryRoutingRule(ParsedQuery query);
