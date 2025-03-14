using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Configuration options for PostgreSQL database connections.
/// </summary>
public class PostgreSqlOptions
{
    /// <summary>
    /// Gets or sets the PostgreSQL connection string.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = "Host=localhost;Database=meshweaver;Username=postgres;Password=postgres";

    /// <summary>
    /// Gets or sets whether to automatically apply migrations at startup.
    /// </summary>
    public bool AutoApplyMigrations { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of connection retries.
    /// </summary>
    public int MaxRetryCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum retry delay in seconds.
    /// </summary>
    public int MaxRetryDelay { get; set; } = 30;

    /// <summary>
    /// Gets or sets the command timeout in seconds.
    /// </summary>
    public int? CommandTimeout { get; set; }

    /// <summary>
    /// Gets or sets whether to enable sensitive data logging.
    /// Should be false in production.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; }
}
