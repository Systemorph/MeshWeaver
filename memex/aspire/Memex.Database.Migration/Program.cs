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

// Wait for Postgres to accept connections AND ensure the target database exists before
// migrating. Two orchestration realities this handles:
//   1. The DB container may report "started" before it is listening (Compose
//      depends_on:service_started, Kubernetes, ACA) — retry with backoff.
//   2. A self-managed Postgres (the pgvector container in Compose/Helm) does NOT pre-create
//      the app database the way managed Azure Postgres does — connect to the maintenance
//      'postgres' database and CREATE it if missing.
// Managed Azure Postgres pre-creates the database and uses a credential provider on the
// data source, so for that path we just probe the data source directly.
var isAzurePg = connectionString.Contains("database.azure.com");
var pgReadyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
while (true)
{
    try
    {
        if (isAzurePg)
        {
            await using var probe = await dataSource.OpenConnectionAsync();
        }
        else
        {
            var targetDb = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "memex";
            var maintenanceCs = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
            await using var admin = new NpgsqlConnection(maintenanceCs);
            await admin.OpenAsync();
            await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", admin);
            check.Parameters.AddWithValue("db", targetDb);
            if (await check.ExecuteScalarAsync() is null)
            {
                logger.LogInformation("Database '{Db}' does not exist — creating it.", targetDb);
                // targetDb is our own configured database name, not user input. Quote-escape defensively.
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{targetDb.Replace("\"", "\"\"")}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
        }
        break;
    }
    catch (Exception ex) when (DateTime.UtcNow < pgReadyDeadline)
    {
        logger.LogInformation("Waiting for Postgres / ensuring database ({Error})", ex.Message);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}
logger.LogInformation("Postgres ready; target database present.");

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
    new V16_NormalizeAccessAssignmentShape(),
    new V17_EnsurePerUserSelfAssignments(),
    new V18_BackfillUserPartitionRegistry(),
    new V19_DeleteLegacyReleaseNodes(),
    new V20_RemoveStrayLegacyUserRows(),
    // v21 retired -- gap preserved so existing prod db_version counters stay monotonic.
    new V22_ConsolidateGlobalCatalogsInAdmin(),
    new V23_PartitionChangesNotify(),
    new V24_DedupMeshNodeNotifyTrigger(),
    new V25_MirrorAccessObjectsToUserSchema(),
    new V26_AddNotificationsSatelliteTable(),
    new V27_RenameUserSchemaToAuthAndMirrorApiTokens(),
    new V28_RenameOrganizationToSpace(),
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
