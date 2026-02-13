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
    /// Vector embedding dimensions (default: 1024 for Cohere embed-v4).
    /// </summary>
    public int VectorDimensions { get; set; } = 1024;

    /// <summary>
    /// Database schema name (default: "public").
    /// </summary>
    public string Schema { get; set; } = "public";
}
