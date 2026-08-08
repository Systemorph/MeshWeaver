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
        new SatelliteTableMapping("_Thread", "threads", "Thread", "ThreadComposer"),
        new SatelliteTableMapping("_ThreadMessage", "threads", "ThreadMessage"),
        new SatelliteTableMapping("_Access", "access", "AccessAssignment"),
        // LEGACY read-only: nothing writes _Tracking satellites any more (tracked changes are
        // projected from the version history). Mapped so rows written by older builds stay readable
        // for the deprecation window — see MeshWeaver.Graph.AnnotationExtensions.
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

    /// <summary>
    /// True if <paramref name="path"/> contains a metadata satellite SEGMENT — one of the
    /// underscore-prefixed <see cref="Defaults"/> segments (<c>_Access</c>, <c>_Thread</c>,
    /// <c>_Activity</c>, <c>_Comment</c>, <c>_Notification</c>, …) that live in a SEPARATE table.
    /// <para>🚨 Matches the EXACT segment, not "any <c>/_</c>": <c>_Policy</c>, <c>_Provider</c> and
    /// other underscore-prefixed nodes are REGULAR <c>mesh_nodes</c> rows (no satellite table), so they
    /// are NOT satellite paths. Treating them as satellites wrongly hid them from content queries —
    /// e.g. a <c>PartitionAccessPolicy</c> at <c>{ns}/_Policy</c> vanished from the permission
    /// evaluator's lookup. <c>Source</c>/<c>Test</c> share the <c>code</c> table but are primary
    /// CONTENT (no leading underscore) and are intentionally not matched here.</para>
    /// </summary>
    public static bool IsSatellitePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (IsSatelliteSegment(seg))
                return true;
        return false;
    }

    /// <summary>
    /// The owning MAIN node of <paramref name="path"/>: everything BEFORE its first satellite
    /// segment — <c>Space/_Access/alice_Access</c> → <c>Space</c>, <c>Space/_Access</c> →
    /// <c>Space</c>, <c>Doc/_Thread/t1/_ThreadMessage/m1</c> → <c>Doc</c>. A path that holds no
    /// satellite segment IS a main node and is returned unchanged; a root-level satellite
    /// (<c>_Access/{id}</c> — the root-scope grant) has no owner and yields <c>""</c>.
    ///
    /// <para>This is what <see cref="MeshNode.MainNode"/> must hold for a satellite, and why the cut
    /// is at the FIRST satellite segment rather than the last: a satellite's MainNode is the node its
    /// permissions DELEGATE to (<c>SatelliteAccessRule</c>) and the prefix its grants project at
    /// (<c>COALESCE(main_node, namespace)</c> in <c>rebuild_user_effective_permissions</c>), so it has
    /// to be a real main node. A MainNode left pointing at a satellite CONTAINER
    /// (<c>{owner}/_Access</c>) names a node that does not exist — it made the access-granted mail
    /// read "you've been given access to X/_Access" and projected the grant one level too deep, below
    /// the node it was granted on.</para>
    /// </summary>
    public static string OwnerOfSatellitePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            if (IsSatelliteSegment(segments[i]))
                return string.Join('/', segments[..i]);
        return path;
    }

    /// <summary>
    /// True if <paramref name="segment"/> is exactly one of the underscore-prefixed
    /// <see cref="Defaults"/> satellite segments — the shared test behind
    /// <see cref="IsSatellitePath"/> and <see cref="OwnerOfSatellitePath"/>.
    /// </summary>
    private static bool IsSatelliteSegment(string segment)
        => segment.Length > 1 && segment[0] == '_'
           && Defaults.Any(m => m.Segment.Length > 0 && m.Segment[0] == '_'
                                && string.Equals(m.Segment, segment, StringComparison.Ordinal));
}
