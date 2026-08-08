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
    /// <para>This is storage-agnostic data carried per namespace; the VALUES (table names) are a
    /// storage-provider concern and are stamped on by the provider (the PG router fills them from
    /// its configurable <c>SatelliteTables</c> options). It is not seeded from any static dictionary.</para>
    /// </summary>
    public Dictionary<string, string>? TableMappings { get; init; }

    /// <summary>
    /// Maps a satellite <c>nodeType</c> to its table — used to resolve the table when a query
    /// carries a <c>nodeType</c> filter but no path (e.g. <c>nodeType:Thread</c> → <c>threads</c>).
    /// Populated alongside <see cref="TableMappings"/> by the storage provider from the same
    /// configurable source; replaces the old static <c>NodeTypeToSuffix</c> chain.
    /// </summary>
    public Dictionary<string, string>? NodeTypeTableMappings { get; init; }

    /// <summary>
    /// Whether this partition tracks node history (versions schema + triggers).
    /// Defaults to true. Set to false for ephemeral partitions (Portal, Kernel).
    /// </summary>
    public bool Versioned { get; init; } = true;

    /// <summary>
    /// Human-readable description of this partition.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>When this partition was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Default <c>ActivityParentPath</c> applied to every executable Code node
    /// in this partition that doesn't set its own. Most useful for shared /
    /// read-only partitions (the docs partition is the canonical example):
    /// set this to <c>"{viewer}"</c> so script runs initiated by a visitor
    /// land in the visitor's home, not in the docs partition itself.
    ///
    /// <para>Per-Code-node config still wins. The resolution order in
    /// <c>CodeNodeType.HandleExecuteScript</c> is:
    /// (1) <c>CodeConfiguration.ActivityParentPath</c>,
    /// (2) <see cref="DefaultActivityParentPath"/> on the partition,
    /// (3) the partition root (current user-home default).</para>
    /// </summary>
    public string? DefaultActivityParentPath { get; init; }

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
    /// Resolves the table name for a given node type via <see cref="NodeTypeTableMappings"/>.
    /// Used when the query has a nodeType filter but no path to infer the satellite table from.
    /// Returns the mapped table if a mapping exists, otherwise the primary table.
    /// </summary>
    public string ResolveTableByNodeType(string? nodeType)
    {
        if (nodeType == null)
            return Table;
        // 1. An explicit per-namespace nodeType→table override wins.
        if (NodeTypeTableMappings != null && NodeTypeTableMappings.TryGetValue(nodeType, out var table))
            return table;
        // 2. Otherwise derive via the standard classification: nodeType → its satellite segment →
        //    THIS def's segment→table map. Lets a def that only carries TableMappings (segment→table)
        //    still resolve a nodeType-filter query, matching the old NodeTypeToSuffix-chain behaviour.
        var segment = SatelliteTableMapping.SegmentForNodeType(nodeType);
        if (segment != null && TableMappings != null && TableMappings.TryGetValue(segment, out var derived))
            return derived;
        return Table;
    }

    /// <summary>
    /// A fresh segment→table map from the configurable standard satellite layout
    /// (<see cref="SatelliteTableMapping.Defaults"/>). Use to populate <see cref="TableMappings"/>
    /// on a standard content partition. Not a static dictionary — a factory over an immutable list.
    /// </summary>
    public static Dictionary<string, string> DefaultSegmentTableMappings()
        => SatelliteTableMapping.ToSegmentTableMap(SatelliteTableMapping.Defaults);

    /// <summary>
    /// A fresh nodeType→table map from the standard satellite layout. Use to populate
    /// <see cref="NodeTypeTableMappings"/> on a standard content partition.
    /// </summary>
    public static Dictionary<string, string> DefaultNodeTypeTableMappings()
        => SatelliteTableMapping.ToNodeTypeTableMap(SatelliteTableMapping.Defaults);

    /// <summary>
    /// True if <paramref name="nodeType"/> is a standard satellite type (resolves to a non-primary
    /// table). Replaces the old <c>NodeTypeToSuffix.ContainsKey</c> checks.
    /// </summary>
    public static bool IsSatelliteNodeType(string? nodeType)
        => nodeType != null && SatelliteTableMapping.Defaults.Any(
            s => s.NodeTypes.Any(nt => string.Equals(nt, nodeType, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Human-readable statement of the <see cref="IsValidPartitionSegment"/> rule, for
    /// composing speaking rejection messages that name the offending id.
    /// </summary>
    public const string PartitionSegmentRequirement =
        "a partition name must start with a letter or digit, contain only letters, digits, "
        + "'.', '-' or '_', and be at most 63 characters";

    /// <summary>
    /// Whether <paramref name="segment"/> is a valid partition name (a top-level id / first
    /// path segment). A partition becomes a backing-store SCHEMA (Postgres/Snowflake), so it
    /// must be a simple identifier: start with a letter/digit, then only
    /// letters/digits/<c>. - _</c>, ≤63 chars (the Postgres identifier limit; kept identical
    /// across backends so a path routable on one is routable on the other). Rejects
    /// URL/query-string-shaped segments (containing <c>? = &amp; % # :</c>, whitespace, …) —
    /// the atioz DB-corruption root cause (2026-06-05, #714): request URLs routed as mesh
    /// paths materialised garbage schemas like <c>login?returnurl=…</c>.
    ///
    /// <para>The ONE charset rule, shared by the storage routers
    /// (<c>PostgreSqlPathRoutingAdapter</c> / <c>SnowflakePathRoutingAdapter</c>: unroutable →
    /// no schema), the provisioning boundary itself
    /// (<c>EnsurePartitionProvisioned</c> / <c>OwnsPartitionProvisioningValidator</c>:
    /// defense-in-depth — a malformed id can never provision a schema no matter which code
    /// path calls it), and the cleanup migration that drops schemas whose names could never
    /// have passed it. (<c>_</c>-prefixed global-satellite namespaces like <c>_Access</c> are
    /// deliberately NOT valid partition segments — their explicit schemas, e.g.
    /// <c>system_access</c>, are registered at boot, never derived from the namespace.)</para>
    /// </summary>
    public static bool IsValidPartitionSegment(string? segment)
        => !string.IsNullOrEmpty(segment)
           && segment.Length <= 63
           && char.IsLetterOrDigit(segment[0])
           && segment.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

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
