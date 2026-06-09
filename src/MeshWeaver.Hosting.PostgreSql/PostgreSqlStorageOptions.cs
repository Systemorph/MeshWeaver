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
    /// Maximum concurrent READ operations the Postgres storage adapter may run
    /// against the shared connection pool. Kept comfortably below the pool's
    /// <c>MaxPoolSize</c> so a synced-query fan-out storm cannot drain the pool
    /// and starve writes (onboarding/chat stay ungated and always have headroom).
    /// See <see cref="ReadConcurrencyGate"/>. This is a per-storage-adapter knob:
    /// in-memory storage has no connection scarcity and is never gated.
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
