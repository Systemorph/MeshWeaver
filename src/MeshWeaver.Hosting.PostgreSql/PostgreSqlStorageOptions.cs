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
}
