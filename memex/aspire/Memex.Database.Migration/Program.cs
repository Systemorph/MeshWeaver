using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex");
else
    builder.AddNpgsqlDataSource("memex");

// Derive vector dimensions from embedding model (passed by AppHost via Embedding__Model)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.Configure<PostgreSqlStorageOptions>(o =>
{
    o.ConnectionString = connectionString;
    o.VectorDimensions = embeddingOptions.Dimensions;
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migration");
var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
var options = host.Services.GetRequiredService<IOptions<PostgreSqlStorageOptions>>();

logger.LogInformation("Running database migration...");

// Grant CREATE on database to azure_pg_admin role so managed identities
// (portal, migration) can create per-organization schemas at runtime.
if (connectionString.Contains("database.azure.com"))
{
    await using var grantCmd = dataSource.CreateCommand(
        "GRANT CREATE ON DATABASE memex TO azure_pg_admin");
    await grantCmd.ExecuteNonQueryAsync();
    logger.LogInformation("Granted CREATE ON DATABASE to azure_pg_admin.");
}

// Initialize the full schema in the public schema (vector extension + all tables).
// Per-partition schemas are created by PostgreSqlPartitionedStoreFactory at app startup,
// but the public schema serves as a fallback since partition data sources use
// SearchPath = "{schemaName},public".
await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options.Value);

// Create satellite tables (threads, annotations, etc.) in the public schema as well.
// These are needed as fallback when partition-specific schemas haven't been created yet.
var satelliteTableNames = MeshWeaver.Mesh.PartitionDefinition.StandardTableMappings.Values;
await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
    dataSource, options.Value, satelliteTableNames);

logger.LogInformation("Database migration completed successfully.");
