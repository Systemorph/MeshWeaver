using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Configuration options for Snowflake storage. Bind from the <c>Snowflake:</c> config section.
/// </summary>
public class SnowflakeStorageOptions
{
    /// <summary>
    /// Snowflake connection string (Snowflake.Data ADO.NET form), e.g.
    /// <c>account=myacct;user=me;password=...;db=meshweaver;warehouse=compute_wh</c>.
    /// For the LocalStack emulator add <c>host=snowflake.localhost.localstack.cloud;port=4566</c>.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Vector embedding dimensions. Synced from <see cref="EmbeddingOptions.Dimensions"/> at startup.
    /// </summary>
    public int VectorDimensions { get; set; } = 1536;

    /// <summary>
    /// Whether the <c>VECTOR(FLOAT, N)</c> column type is used. <c>null</c> (default) probes the
    /// server once at schema initialization (<see cref="SnowflakeCapabilityProbe"/>) —
    /// real Snowflake supports it; the LocalStack emulator may not. When false, no embedding column
    /// is created and free-text queries stay on the ILIKE path.
    /// </summary>
    public bool? EnableVectorType { get; set; }

    /// <summary>
    /// Database schema name for central tables (default: "public").
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// Schema holding the durable event log (default: "events").
    /// </summary>
    public string EventsSchema { get; set; } = "events";

    /// <summary>
    /// Enables the cross-process change-feed poller over <c>events.event_log</c>. Snowflake has no
    /// LISTEN/NOTIFY, so cross-silo change propagation polls the event log; in-process changes are
    /// always published synchronously from Write/Delete regardless of this flag. Polling keeps the
    /// warehouse warm — this flag and <see cref="ChangeFeedPollInterval"/> are the cost knobs.
    /// </summary>
    public bool EnableChangeFeedPolling { get; set; } = true;

    /// <summary>
    /// Poll cadence of the cross-process change-feed poller. A floor, not a target: a full page
    /// (500 events) triggers an immediate drain re-poll before the interval resumes.
    /// </summary>
    public TimeSpan ChangeFeedPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum concurrent READ operations, mirrored from the PG option of the same name. The actual
    /// cap lives in <see cref="MeshWeaver.Mesh.Threading.IoPoolOptions.SnowflakeRead"/> (the
    /// <c>sf-read:{adapter}</c> pool); this legacy-shaped knob is retained for config compatibility.
    /// </summary>
    public int MaxReadConcurrency { get; set; } = 16;

    /// <summary>
    /// Satellite-table mapping — same semantics as the PostgreSQL option: within a partition's
    /// schema, nodes whose path carries a satellite segment (e.g. <c>_Thread</c>) — or, pathless,
    /// whose <c>nodeType</c> maps to one — live in a dedicated table instead of <c>mesh_nodes</c>.
    /// </summary>
    public IReadOnlyList<SatelliteTableMapping> SatelliteTables { get; init; }
        = SatelliteTableMapping.Defaults;
}
