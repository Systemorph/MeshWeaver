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
    /// When <see langword="true"/> (partitioned Postgres), the provider <b>defers</b> the query
    /// shapes the native <c>PostgreSqlPartitionedMeshQuery</c> owns — emitting an empty Initial,
    /// contributing no rows — so its <c>ListChildPaths</c> scope-walk (the 60-70s onboarding/storm
    /// stall) is removed for those shapes:
    /// <list type="bullet">
    ///   <item><b>Unscoped / wildcard-first-segment</b> → the native provider fans out across
    ///     partitions.</item>
    ///   <item><b>Scoped primary (<c>mesh_nodes</c>) reads</b> → the native provider delegates
    ///     to a per-schema <c>PostgreSqlMeshQuery</c> over the cached adapter (live deltas).</item>
    /// </list>
    /// It STILL serves <b>scoped satellite reads</b> (a <c>_</c>-prefixed path segment, a
    /// satellite nodeType, or <c>source:activity</c>/<c>accessed</c>): the native delegate's
    /// satellite Query Initial under-returns pre-existing rows, so the pedestrian remains the
    /// live server for those (a follow-up will move them too). No rows are dropped.
    ///
    /// <para>The pedestrian stays registered (it backs the <c>IMeshQueryCore</c> fan-in shape +
    /// <c>Select</c>/exact-path probes). Absent (in-memory / file-system / single-schema backends)
    /// → the pedestrian is the only query provider and behaves unchanged.</para>
    /// </summary>
    public bool DeferToNativeProvider { get; init; }
}
