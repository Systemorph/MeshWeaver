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
        // An activity's sealed log slices ({activityPath}/_Log/{index}) — same table as the activity
        // they belong to, so a partition's activity data stays in one place and no new table is
        // provisioned. The path already contains _Activity, so segment resolution agrees either way;
        // the entry is what makes a nodeType-filtered query find them.
        new SatelliteTableMapping("_Log", "activities", "ActivityLogSegment"),
        new SatelliteTableMapping("_UserActivity", "user_activities", "UserActivity"),
        new SatelliteTableMapping("_Thread", "threads", "Thread", "ThreadComposer"),
        new SatelliteTableMapping("_ThreadMessage", "threads", "ThreadMessage"),
        new SatelliteTableMapping("_Access", "access", "AccessAssignment"),
        // LEGACY read-only: nothing writes _Tracking satellites any more (tracked changes are
        // projected from the version history). Mapped so rows written by older builds stay readable
        // for the deprecation window — see AnnotationExtensions in the MeshWeaver.Markdown.Collaboration module.
        new SatelliteTableMapping("_Tracking", "annotations", "TrackedChange"),
        // "Approval" is the RETIRED platform type; "Approvals/Approval" is the node-native
        // package type that replaced it. Both are listed so a nodeType-filtered query finds
        // rows written by either — placement itself is resolved from the _Approval SEGMENT,
        // which is why retiring the platform type moved no data.
        new SatelliteTableMapping("_Approval", "annotations", "Approval", "Approvals/Approval"),
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
    /// The satellite CONTAINER of <paramref name="path"/>: everything up to AND INCLUDING its FIRST
    /// satellite segment — <c>Doc/_Thread/t1/_ThreadMessage/m1</c> → <c>Doc/_Thread</c>,
    /// <c>Doc/_Comment/c1</c> → <c>Doc/_Comment</c>. <c>null</c> when the path holds no satellite
    /// segment (it is a main-node path, served by the primary table).
    ///
    /// <para>🚨 This is the only path shape a satellite READ can be expressed as on EVERY backend,
    /// which is why it exists. A content query is resolved to ONE storage table: on Postgres from
    /// the query path's satellite segment (or a satellite <c>nodeType</c> filter), and in the
    /// in-repo <c>StorageAdapterMeshQueryProvider</c> from the same signals via
    /// <c>IsSatelliteTargetedQuery</c>. So <c>path:{main} scope:subtree</c> reads <c>mesh_nodes</c>
    /// and returns NO metadata satellites on either backend, while <c>path:{container}
    /// scope:subtree</c> reads the satellite's own table and returns all of them — the container
    /// and everything nested under it, other satellite kinds included.</para>
    ///
    /// <para>Pair it with <see cref="OwnerOfSatellitePath"/>, which returns the main node this
    /// container hangs off: the two split one path at the same seam. Content satellites
    /// (<c>Source</c>/<c>Test</c> → <c>code</c>, no leading underscore) are deliberately NOT
    /// containers here — they are primary CONTENT and every backend's primary-table query already
    /// unions their table in.</para>
    /// </summary>
    public static string? SatelliteContainerOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            if (IsSatelliteSegment(segments[i]))
                return string.Join('/', segments[..(i + 1)]);
        return null;
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

    /// <summary>
    /// True if <paramref name="id"/> is a <b>satellite-shaped node id</b>: an underscore followed by
    /// an upper-case letter (<c>_Policy</c>, <c>_Provider</c>, <c>_GitSync</c>,
    /// <c>_DefaultInstallLedger</c>, <c>_Entitlements</c>, …). A node with such an id is a SIBLING
    /// satellite — governance bookkeeping filed next to the node it belongs to — so its
    /// <see cref="MeshNode.MainNode"/> is its namespace, never itself (#2383).
    ///
    /// <para>This is the repo-wide convention, not a new one: <c>StaticRepoImporter</c>,
    /// <c>InstanceSyncService</c>, <c>GitHubSyncService</c>, <c>PackageInstaller</c>,
    /// <c>DeleteLayoutArea</c>, <c>MarkdownOverviewLayoutArea</c> and the bulk-create refusal all
    /// classify a <c>_</c>-prefixed segment exactly this way. <c>CachingStorageAdapter</c>'s
    /// <c>ExtractMainNodePath</c> uses the same underscore+upper-case test.</para>
    ///
    /// <para>🚨 Deliberately DIFFERENT from <see cref="IsSatelliteSegment"/>, and the two must not be
    /// merged. That one answers <i>"which TABLE does this row live in"</i> and is therefore
    /// restricted to the enumerated <see cref="Defaults"/> — widening it would move <c>_Policy</c>
    /// out of <c>mesh_nodes</c> and hide it from the permission evaluator's
    /// <c>id = '_Policy'</c> lookup, the regression the remark on <see cref="IsSatellitePath"/>
    /// records. This one answers <i>"does this node belong to the node above it"</i>, which is a
    /// question about PARENTAGE, not storage. <c>_Policy</c> is a <c>mesh_nodes</c> row AND a
    /// satellite; both are true at once.</para>
    ///
    /// <para>🚨 The test is on the node's OWN ID, never on its whole path — because a PARTITION may
    /// legitimately be named with a leading underscore. <c>GlobalSettingsNodeType.SettingsPath</c> is
    /// literally <c>_Setting</c>, so scanning the path would resolve <c>_Setting/_Policy</c>'s owner
    /// to <c>""</c>, the empty prefix that <c>COALESCE(main_node, namespace)</c> projects as a
    /// ROOT-scope grant. The owner comes from <see cref="OwnerOfSatellitePath"/> over the NAMESPACE,
    /// which leaves a non-container namespace like <c>_Setting</c> intact.</para>
    /// </summary>
    public static bool IsSatelliteId(string? id)
        => id is { Length: > 1 } && id[0] == '_' && char.IsUpper(id[1]);

    /// <summary>
    /// The namespace of the partition CATALOG — <c>Admin/Partition/{partitionName}</c>. Entries there
    /// are partition DECLARATIONS, so their id is a name being declared rather than a relationship
    /// being expressed. Mirrors <c>PartitionNodeType.Namespace</c>, which lives in MeshWeaver.Graph
    /// and so cannot be referenced from here.
    /// </summary>
    private const string PartitionCatalogNamespace = "Admin/Partition";

    /// <summary>
    /// True if <paramref name="node"/> is a SIBLING satellite — governance bookkeeping filed next to
    /// the node it belongs to, whose <see cref="MeshNode.MainNode"/> is therefore its namespace and
    /// never itself (#2383). The ONE definition of that classification, shared by the create/upsert
    /// normalization and by the guard that sweeps the static seeds; a second, subtly different copy
    /// would give a subtly different inventory.
    ///
    /// <para>Three conditions, and each excludes a real node that would otherwise be misread:</para>
    /// <list type="number">
    ///   <item>The id is satellite-shaped (<see cref="IsSatelliteId"/>).</item>
    ///   <item>🚨 It HAS a namespace. A partition root is a main node even when its own name begins
    ///     with an underscore — <c>GlobalSettingsNodeType.SettingsPath</c> is literally
    ///     <c>_Setting</c> — and <c>MainNode == Path</c> is correct for it.</item>
    ///   <item>🚨 It is not an entry in the partition CATALOG. <c>Admin/Partition/_Access</c>
    ///     DECLARES the global root-scope access partition; its id is that partition's NAME. It is a
    ///     main node of the catalog that lists it, and re-pointing its MainNode would drop it out of
    ///     the catalog's own <c>is:main</c> listing — turning a fix into a disappearance.</item>
    /// </list>
    /// </summary>
    public static bool IsSiblingSatellite(MeshNode node)
        => node is not null
           && !string.IsNullOrEmpty(node.Namespace)
           && IsSatelliteId(node.Id)
           && !string.Equals(node.Namespace, PartitionCatalogNamespace, StringComparison.OrdinalIgnoreCase);
}
