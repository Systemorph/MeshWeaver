namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Behaviour switches for <see cref="StorageAdapterMeshQueryProvider"/>, supplied
/// via DI by the hosting backend.
/// </summary>
/// <remarks>
/// Registered as a singleton ONLY by backends that pair the pedestrian provider
/// with a native fan-out provider that already serves unscoped + satellite-routed
/// queries (partitioned Postgres → <c>PostgreSqlPartitionedMeshQuery</c>). When the
/// option is absent the provider behaves exactly as before — it's the only query
/// provider for in-memory / file-system / single-schema backends.
/// </remarks>
public sealed record StorageAdapterQueryProviderOptions
{
    /// <summary>
    /// When <see langword="true"/>, the provider <b>defers</b> (emits an empty
    /// Initial and contributes no rows) for queries another provider in the fan-in
    /// already owns: <em>unscoped / wildcard-first-segment</em> queries (the native
    /// provider fans out across partitions) and <em>satellite-routed</em> queries
    /// (the pedestrian only ever walks <c>mesh_nodes</c> and never visited satellite
    /// tables, so it returned empty anyway — just after a slow scope walk). It still
    /// serves <em>scoped <c>mesh_nodes</c></em> queries, which the native provider
    /// short-circuits to empty.
    ///
    /// <para>This removes the pedestrian's redundant <c>ListChildPaths</c> walk from
    /// the query-merge for those query shapes — the walk that gated the cross-schema
    /// <c>ResolvePath</c>/onboarding stall by 60-70s — without dropping any rows.</para>
    /// </summary>
    public bool DeferUnscopedAndSatelliteToNativeProvider { get; init; }
}
