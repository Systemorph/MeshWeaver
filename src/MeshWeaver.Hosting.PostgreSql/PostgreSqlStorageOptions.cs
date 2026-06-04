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
}
