namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Configuration options for PostgreSQL storage.
/// </summary>
public class PostgreSqlStorageOptions
{
    /// <summary>
    /// PostgreSQL connection string.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Vector embedding dimensions. Synced from <see cref="EmbeddingOptions.Dimensions"/> at startup.
    /// </summary>
    public int VectorDimensions { get; set; } = 1536;

    /// <summary>
    /// Database schema name (default: "public").
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// Maximum concurrent READ operations the Postgres storage adapter may run against the shared
    /// connection pool, kept comfortably below the pool's <c>MaxPoolSize</c> so a synced-query
    /// fan-out storm cannot drain the pool and starve writes (onboarding/chat stay ungated and
    /// always have headroom).
    ///
    /// <para>🚦 The read bound is now enforced by the per-adapter READ I/O pool
    /// (<c>pg-read:{adapter}</c>) — the former hand-woven <c>ReadConcurrencyGate</c> folded into the
    /// one sanctioned <see cref="MeshWeaver.Mesh.Threading.IIoPool"/> primitive. The actual cap lives
    /// in <see cref="MeshWeaver.Mesh.Threading.IoPoolOptions.PostgresRead"/> (override via
    /// <c>AddIoPools(o =&gt; o with { PostgresRead = N })</c>); this legacy knob is retained for config
    /// compatibility. In-memory storage has no connection scarcity and falls back to the unbounded pool.</para>
    /// </summary>
    public int MaxReadConcurrency { get; set; } = 16;

    /// <summary>
    /// Satellite-table mapping — a <b>PostgreSQL storage-provider implementation detail</b>,
    /// configurable per host, NOT a static dictionary and NOT baked into the storage-agnostic
    /// <see cref="MeshWeaver.Mesh.PartitionDefinition"/>. Within a partition's schema, nodes whose
    /// path carries a satellite segment (e.g. <c>_Thread</c>, <c>_Approval</c>) — or, when there is
    /// no path, whose <c>nodeType</c> maps to one — live in a dedicated table instead of the primary
    /// <c>mesh_nodes</c>. The PG router stamps these onto every <see cref="MeshWeaver.Mesh.PartitionDefinition"/>
    /// it routes, so <b>search, query, and routing all resolve the right table</b>. Override to add a
    /// custom satellite type's table; other backends (Cosmos, in-memory) simply have no satellites.
    /// </summary>
    public IReadOnlyList<MeshWeaver.Mesh.SatelliteTableMapping> SatelliteTables { get; init; }
        = MeshWeaver.Mesh.SatelliteTableMapping.Defaults;
}
