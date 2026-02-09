namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Options for configuring the Cosmos DB data seeder.
/// </summary>
public class CosmosSeederOptions
{
    /// <summary>
    /// Whether seeding is enabled. Default: false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to the seed data directory (e.g., samples/Graph/Data).
    /// </summary>
    public string? SeedDataPath { get; set; }

    /// <summary>
    /// Override the idempotency check and force re-seeding.
    /// </summary>
    public bool ForceReseed { get; set; }
}
