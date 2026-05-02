using Memex.Database.Migration.Migrations;
using Memex.Portal.ServiceDefaults;
using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

// Database migration is split in three phases:
//
//   1. Schema initialization (idempotent, ALWAYS runs)
//      Creates/updates tables, indexes, triggers, satellite tables, and the admin schema.
//      Brings any DB — fresh or existing — to the latest schema definition.
//
//   2. Versioned data repairs (one-shot, ONLY for existing DBs)
//      Each migration fixes data written incorrectly by a prior code version. Tracked via
//      MeshNode(id="db_version") in admin.mesh_nodes. Fresh DBs skip ALL repairs and
//      fast-forward to the latest version (there is no legacy data to repair).
//
//   3. Searchable-schemas refresh (idempotent, always runs)
//      Repopulates public.searchable_schemas from the current set of content partitions.
//
// To add a new repair: drop a `Vxx_*.cs` file under Migrations/ implementing IMigration
// and add it to the list passed to MigrationRunner below.

Console.WriteLine("[Migration] Starting...");
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
Console.WriteLine($"[Migration] ConnectionString: {(string.IsNullOrEmpty(connectionString) ? "(empty)" : connectionString[..Math.Min(30, connectionString.Length)] + "...")}");
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex");
else
    builder.AddNpgsqlDataSource("memex");

// Vector dimensions come from the embedding model (passed by AppHost via Embedding__Model).
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.Configure<PostgreSqlStorageOptions>(o =>
{
    o.ConnectionString = connectionString;
    o.VectorDimensions = embeddingOptions.Dimensions;
});

Console.WriteLine("[Migration] Building host...");
var host = builder.Build();
Console.WriteLine("[Migration] Host built. Resolving services...");

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migration");
var dataSource = host.Services.GetRequiredService<NpgsqlDataSource>();
var options = host.Services.GetRequiredService<IOptions<PostgreSqlStorageOptions>>().Value;
logger.LogInformation("Running database migration...");

// ── Phase 1: Schema initialization (always runs)
var initResult = await SchemaInitialization.RunAsync(dataSource, options, connectionString, logger);

// ── Phase 2: Versioned data repairs
var migrations = new IMigration[]
{
    new V01_MoveAccessAssignments(),
    new V02_RebuildTriggerFunctions(),
    new V03_DropRogueSchemas(),
    new V04_UpgradeViewerToAdmin(),
    new V05_EnsureUserSelfAssignments(),
    new V06_FixSearchAcrossSchemas(),
    new V07_PerUserPermissionRebuildTrigger(),
    new V08_FixThreadMessageMainNode(),
    new V09_RenameSourceTestSegments(),
    new V10_PerUserPartitions(),
    new V11_RewriteApiTokenPaths(),
    // v12 was retired — see V13_RebuildPermissionsForApiBitmask for context.
    new V13_RebuildPermissionsForApiBitmask(),
    new V14_AddPartitionPrefixToNamespaces(),
    new V15_FinalUserSchemaCleanup(),
};

var ctx = new MigrationContext(dataSource, connectionString, options, logger, initResult.IsFreshDb);
var runner = new MigrationRunner(migrations);
var finalVersion = await runner.RunAsync(ctx);

// ── Phase 3: Searchable-schemas refresh (always runs)
await SearchableSchemasUpdater.RunAsync(dataSource, logger);

logger.LogInformation("Database migration completed. Version: {Version}", finalVersion);

// Signal completion to Aspire (health check passes, then process exits cleanly)
using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await host.StartAsync(shutdownCts.Token);
await host.StopAsync(shutdownCts.Token);
Environment.Exit(0);
