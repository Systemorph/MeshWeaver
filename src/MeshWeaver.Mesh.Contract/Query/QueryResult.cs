namespace MeshWeaver.Mesh;

/// <summary>
/// Single query-result row. Carries every MeshNode shell field EXCEPT
/// <see cref="MeshNode.Content"/> — query rows are stale by design
/// (see <c>feedback_query_content_stale.md</c>). Use
/// <c>workspace.GetMeshNodeStream(Path)</c> to read live content.
/// <para>
/// Score / PathDistance / Highlights / ProviderName carry the per-row scoring
/// + provenance that the aggregator uses to dedupe (by <see cref="Path"/>) and
/// re-sort across multiple <see cref="Services.IMeshQueryProvider"/> emissions.
/// </para>
/// </summary>
public record QueryResult
{
    /// <summary>Full node path (Namespace + Id).</summary>
    public required string Path { get; init; }
    /// <summary>Local id within the namespace.</summary>
    public string? Id { get; init; }
    /// <summary>Parent namespace (everything before the last <c>/</c>).</summary>
    public string? Namespace { get; init; }
    /// <summary>Display name.</summary>
    public string? Name { get; init; }
    /// <summary>Long-form description.</summary>
    public string? Description { get; init; }
    /// <summary>Node type (e.g. "Thread", "Agent", "Markdown").</summary>
    public string? NodeType { get; init; }
    /// <summary>Category bucket for UI grouping.</summary>
    public string? Category { get; init; }
    /// <summary>Primary node path for satellite cells; equals <see cref="Path"/> for main nodes.</summary>
    public string? MainNode { get; init; }
    /// <summary>Icon (filename or inline SVG).</summary>
    public string? Icon { get; init; }
    /// <summary>Display order hint.</summary>
    public int? Order { get; init; }
    /// <summary>When the node was first created.</summary>
    public DateTimeOffset CreatedDate { get; init; }
    /// <summary>User identifier of the creator.</summary>
    public string? CreatedBy { get; init; }
    /// <summary>Last update timestamp.</summary>
    public DateTimeOffset LastModified { get; init; }
    /// <summary>User identifier of the most recent updater.</summary>
    public string? LastModifiedBy { get; init; }
    /// <summary>Monotonic version counter.</summary>
    public long Version { get; init; }
    /// <summary>Lifecycle state (Active / Transient / Deleted).</summary>
    public MeshNodeState State { get; init; } = MeshNodeState.Active;

    /// <summary>Higher = stronger match. Scale is comparable across providers for a single query.</summary>
    public double Score { get; init; }

    /// <summary>Distance in path segments from the request's <c>ContextPath</c>; null when no proximity dimension applies.</summary>
    public int? PathDistance { get; init; }

    /// <summary>Which <see cref="Services.IMeshQueryProvider"/> emitted this row. Used for dedup-tiebreak + diagnostics.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Matched-text spans for UI highlight rendering; null when not computed.</summary>
    public IReadOnlyList<string>? Highlights { get; init; }

    /// <summary>
    /// Projection helper — copies every shell field off <paramref name="node"/> and
    /// drops <see cref="MeshNode.Content"/>. Convenience for providers that already
    /// have a hydrated MeshNode at hand (e.g. from a JOIN row).
    /// </summary>
    public static QueryResult FromNode(
        MeshNode node, double score = 0,
        int? pathDistance = null,
        string? providerName = null,
        IReadOnlyList<string>? highlights = null)
        => new()
        {
            Path = node.Path ?? string.Empty,
            Id = node.Id,
            Namespace = node.Namespace,
            Name = node.Name,
            Description = node.Description,
            NodeType = node.NodeType,
            Category = node.Category,
            MainNode = node.MainNode,
            Icon = node.Icon,
            Order = node.Order,
            CreatedDate = node.CreatedDate,
            CreatedBy = node.CreatedBy,
            LastModified = node.LastModified,
            LastModifiedBy = node.LastModifiedBy,
            Version = node.Version,
            State = node.State,
            Score = score,
            PathDistance = pathDistance,
            ProviderName = providerName,
            Highlights = highlights,
        };
}
