namespace MeshWeaver.Mesh;

/// <summary>
/// One satellite type's storage placement: the path <see cref="Segment"/> (e.g. <c>"_Thread"</c>),
/// the <see cref="Table"/> it lives in (e.g. <c>"threads"</c>), and the <see cref="NodeTypes"/> that
/// resolve to it when a query carries a <c>nodeType</c> filter but no path.
///
/// <para>This replaces the old static <c>PartitionDefinition.StandardTableMappings</c> /
/// <c>NodeTypeToSuffix</c> dictionaries. The set of mappings is <b>configurable</b> (per host via
/// <c>PostgreSqlStorageOptions.SatelliteTables</c>, and per namespace via
/// <see cref="PartitionDefinition.TableMappings"/> / <see cref="PartitionDefinition.NodeTypeTableMappings"/>),
/// not hardcoded. <see cref="Defaults"/> is a <c>static readonly</c> immutable LIST — the allowed kind
/// of static (a constant lookup, never written at runtime), NOT a static mutable dictionary.</para>
/// </summary>
public sealed record SatelliteTableMapping(string Segment, string Table, params string[] NodeTypes)
{
    /// <summary>
    /// The standard satellite layout shared by content partitions (User, Space, org). DEFAULT values
    /// for <c>PostgreSqlStorageOptions.SatelliteTables</c>; a host may replace them.
    /// <c>_Thread</c>/<c>_ThreadMessage</c> share the <c>threads</c> table; <c>_Comment</c>/
    /// <c>_Approval</c>/<c>_Tracking</c> share <c>annotations</c>; <c>Source</c>/<c>Test</c> are primary
    /// code content sharing the <c>code</c> table (no leading underscore — matched as a path segment,
    /// and not nodeType-resolvable).
    /// </summary>
    public static IReadOnlyList<SatelliteTableMapping> Defaults { get; } =
    [
        new SatelliteTableMapping("_Activity", "activities", "Activity"),
        new SatelliteTableMapping("_UserActivity", "user_activities", "UserActivity"),
        new SatelliteTableMapping("_Thread", "threads", "Thread"),
        new SatelliteTableMapping("_ThreadMessage", "threads", "ThreadMessage"),
        new SatelliteTableMapping("_Access", "access", "AccessAssignment"),
        new SatelliteTableMapping("_Tracking", "annotations", "TrackedChange"),
        new SatelliteTableMapping("_Approval", "annotations", "Approval"),
        new SatelliteTableMapping("_Comment", "annotations", "Comment"),
        new SatelliteTableMapping("_Notification", "notifications", "Notification"),
        new SatelliteTableMapping("Source", "code"),
        new SatelliteTableMapping("Test", "code"),
    ];

    /// <summary>Builds a fresh segment→table map (path-based resolution) from a mapping set.</summary>
    public static Dictionary<string, string> ToSegmentTableMap(IEnumerable<SatelliteTableMapping> mappings)
        => mappings.ToDictionary(m => m.Segment, m => m.Table, StringComparer.Ordinal);

    /// <summary>Builds a fresh nodeType→table map (nodeType-filter resolution) from a mapping set.</summary>
    public static Dictionary<string, string> ToNodeTypeTableMap(IEnumerable<SatelliteTableMapping> mappings)
        => mappings
            .SelectMany(m => m.NodeTypes.Select(nt => (nt, m.Table)))
            .ToDictionary(x => x.nt, x => x.Table, StringComparer.OrdinalIgnoreCase);

    /// <summary>The standard satellite path segment for a nodeType (e.g. <c>"Approval"</c> → <c>"_Approval"</c>), or null.</summary>
    public static string? SegmentForNodeType(string nodeType)
        => Defaults.FirstOrDefault(
            m => m.NodeTypes.Any(nt => string.Equals(nt, nodeType, StringComparison.OrdinalIgnoreCase)))?.Segment;
}
