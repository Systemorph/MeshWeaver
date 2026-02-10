namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Options for configuring Cosmos DB storage.
/// </summary>
public class CosmosStorageOptions
{
    /// <summary>
    /// Cosmos DB connection string. Read from Aspire's configuration
    /// (e.g., ConnectionStrings:memexcosmos) or set directly.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Cosmos DB database name. Default: "MeshWeaver".
    /// </summary>
    public string DatabaseName { get; set; } = "MeshWeaver";

    /// <summary>
    /// Container name for MeshNodes. Default: "nodes".
    /// </summary>
    public string NodesContainerName { get; set; } = "nodes";

    /// <summary>
    /// Container name for partition objects. Default: "partitions".
    /// </summary>
    public string PartitionsContainerName { get; set; } = "partitions";
}
