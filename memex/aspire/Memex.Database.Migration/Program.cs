using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.Hosting.PostgreSql;
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

// Initialize the full schema in the public schema (vector extension + all tables).
// Per-partition schemas are created by PostgreSqlPartitionedStoreFactory at app startup,
// but the public schema serves as a fallback since partition data sources use
// SearchPath = "{schemaName},public".
await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options.Value);

logger.LogInformation("Database migration completed successfully.");
