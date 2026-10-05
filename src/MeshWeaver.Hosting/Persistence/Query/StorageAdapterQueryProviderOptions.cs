namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Behaviour switches for <see cref="StorageAdapterMeshQueryProvider"/>, supplied
/// via DI by the hosting backend.
/// </summary>
/// <remarks>
/// Registered as a singleton ONLY by backends that pair the pedestrian provider
/// with a native fan-out provider that already serves unscoped + satellite-routed
/// queries (partitioned Postgres → <c>PostgreSqlPartitionedMeshQuery</c>, partitioned Snowflake →
/// <c>SnowflakePartitionedMeshQuery</c>). When the
/// option is absent the provider behaves exactly as before — it's the only query
/// provider for in-memory / file-system / single-schema backends.
/// </remarks>
public sealed record StorageAdapterQueryProviderOptions
{
    /// <summary>
    /// When <see langword="true"/> (partitioned Postgres), the provider <b>defers</b> the query
    /// shapes the native <c>PostgreSqlPartitionedMeshQuery</c> owns — emitting an empty Initial,
    /// contributing no rows — so its <c>ListChildPaths</c> scope-walk (the 60-70s onboarding/storm
    /// stall) is removed for those shapes:
    /// <list type="bullet">
    ///   <item><b>Unscoped / wildcard-first-segment</b> → the native provider fans out across
    ///     partitions.</item>
    ///   <item><b>Scoped primary (<c>mesh_nodes</c>) reads</b> → the native provider delegates
    ///     to a per-schema <c>PostgreSqlMeshQuery</c> over the cached adapter (live deltas).</item>
    ///   <item><b>Scoped satellite reads</b> (a <c>_</c>-prefixed path segment or a satellite
    ///     nodeType) → the same per-schema delegate serves them live. The pedestrian's walk could
    ///     never see the satellite tables, and on a large partition it flooded the shared
    ///     <c>pg-read:</c> pool (Doc/Architecture/QueryFanInStallTerminal).</item>
    /// </list>
    /// It STILL serves scoped <c>source:activity</c> / <c>source:accessed</c>: the native provider
    /// answers those from its cross-schema fan-out, which is one-shot on Snowflake.
    ///
    /// <para>The pedestrian stays registered (it backs the <c>IMeshQueryCore</c> fan-in shape +
    /// <c>Select</c>/exact-path probes). Absent (in-memory / file-system / single-schema backends)
    /// → the pedestrian is the only query provider and behaves unchanged.</para>
    /// </summary>
    public bool DeferToNativeProvider { get; init; }
}
