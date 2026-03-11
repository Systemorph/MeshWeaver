namespace MeshWeaver.Mesh;

/// <summary>
/// Content type for Partition nodes that define storage partition metadata.
/// Partition nodes live at Admin/Partition/{Name}.
/// Each partition maps a namespace (first path segment) to a data source, schema, and table.
/// Satellite nodes (Activity, UserActivity, etc.) are stored in the same schema as their parent,
/// routed to dedicated tables via <see cref="TableMappings"/>.
/// </summary>
public record PartitionDefinition
{
    /// <summary>
    /// The namespace (first path segment) this partition serves. E.g., "Admin", "User".
    /// </summary>
    public string Namespace { get; init; } = "";

    /// <summary>
    /// Named data source identifier. Maps to a registered connection via MeshBuilder configuration.
    /// </summary>
    public string DataSource { get; init; } = "default";

    /// <summary>
    /// For PostgreSQL: the schema name. For FileSystem: the relative directory path.
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// The primary table name within the schema. Defaults to "mesh_nodes".
    /// </summary>
    public string Table { get; init; } = "mesh_nodes";

    /// <summary>
    /// Maps path segment suffixes to table names within the same schema.
    /// Satellite nodes whose path contains a matching segment (e.g., "_Activity")
    /// are stored in the mapped table instead of the primary <see cref="Table"/>.
    /// Keys are segment prefixes like "_Activity", values are table names like "activities".
    /// </summary>
    public Dictionary<string, string>? TableMappings { get; init; }

    /// <summary>
    /// Whether this partition tracks node history (versions schema + triggers).
    /// Defaults to true. Set to false for ephemeral partitions (Portal, Kernel).
    /// </summary>
    public bool Versioned { get; init; } = true;

    /// <summary>
    /// Human-readable description of this partition.
    /// </summary>
    public string? Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Standard satellite table mappings shared by all content partitions (User, org partitions).
    /// Each satellite type gets its own dedicated table within the same schema.
    /// The main mesh_nodes table only contains primary entities (MainNode == Path).
    /// </summary>
    public static Dictionary<string, string> StandardTableMappings => new()
    {
        ["_Activity"] = "activities",
        ["_UserActivity"] = "user_activities",
        ["_Thread"] = "threads",
        ["_TrackedChange"] = "tracked_changes",
        ["_Approval"] = "approvals",
        ["_AccessAssignment"] = "access_assignments",
        ["_Comment"] = "comments",
    };

    /// <summary>
    /// Resolves the table name for a given node path based on <see cref="TableMappings"/>.
    /// Returns the mapped table if the path contains a matching satellite segment, otherwise the primary table.
    /// Matches longest suffix first to avoid "_Thread" matching "_ThreadMessage" paths.
    /// </summary>
    public string ResolveTable(string path)
    {
        if (TableMappings != null)
        {
            // Sort by key length descending so longer suffixes match first
            // (e.g., "_ThreadMessage" before "_Thread")
            foreach (var (suffix, table) in TableMappings.OrderByDescending(kv => kv.Key.Length))
            {
                if (PathContainsSegment(path, suffix))
                    return table;
            }
        }
        return Table;
    }

    /// <summary>
    /// Checks if a path contains the given segment as a complete path component.
    /// The segment must appear after a '/' and be followed by '/' or end of string.
    /// </summary>
    private static bool PathContainsSegment(string path, string segment)
    {
        var idx = 0;
        while (idx < path.Length)
        {
            var pos = path.IndexOf(segment, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;

            // Must be preceded by '/' or be at the start
            var atStart = pos == 0 || path[pos - 1] == '/';
            // Must be followed by '/' or end of string
            var end = pos + segment.Length;
            var atEnd = end >= path.Length || path[end] == '/';

            if (atStart && atEnd) return true;
            idx = pos + 1;
        }
        return false;
    }
}
