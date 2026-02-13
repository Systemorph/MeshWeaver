using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector.Npgsql;

// Parse arguments
string? sourcePath = null;
string? connectionString = null;
bool force = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--source-path" when i + 1 < args.Length:
            sourcePath = args[++i];
            break;
        case "--connection-string" when i + 1 < args.Length:
            connectionString = args[++i];
            break;
        case "--force":
            force = true;
            break;
        case "--help":
            PrintUsage();
            return 0;
    }
}

if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(connectionString))
{
    Console.Error.WriteLine("Error: --source-path and --connection-string are required.");
    PrintUsage();
    return 1;
}

if (!Directory.Exists(sourcePath))
{
    Console.Error.WriteLine($"Error: Source path does not exist: {sourcePath}");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger<StorageImporter>();

// Set up source (file system)
var source = new FileSystemStorageAdapter(sourcePath);

// Build serialization options matching the hub's pipeline
var jsonOptions = StorageImporter.CreateFullImportOptions();

// Set up target (PostgreSQL)
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
await using var dataSource = dataSourceBuilder.Build();

Console.WriteLine($"Connecting to PostgreSQL...");

// Initialize schema (idempotent)
var storageOptions = new PostgreSqlStorageOptions { ConnectionString = connectionString };
await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, storageOptions);
Console.WriteLine("Schema initialized.");

var target = new PostgreSqlStorageAdapter(dataSource);

// Idempotency check
if (!force)
{
    var exists = await target.ExistsAsync("Organization");
    if (exists)
    {
        logger.LogInformation("PostgreSQL already contains data. Use --force to re-import.");
        return 0;
    }
}

// Run import
var importer = new StorageImporter(source, target, logger);
var result = await importer.ImportAsync(new StorageImportOptions
{
    JsonOptions = jsonOptions,
    OnProgress = (nodes, partitions, path) =>
    {
        if (nodes % 25 == 0)
            Console.WriteLine($"  Progress: {nodes} nodes, {partitions} partitions (current: {path})");
    }
});

Console.WriteLine($"Import complete: {result.NodesImported} nodes, {result.PartitionsImported} partitions in {result.Elapsed.TotalSeconds:F1}s");
if (result.NodesSkipped > 0 || result.PartitionsSkipped > 0)
{
    Console.Error.WriteLine($"Warning: {result.NodesSkipped} nodes skipped, {result.PartitionsSkipped} partitions skipped");
    return 2;
}
return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: dotnet run --project tools/MeshWeaver.Tools.PostgreSqlImport -- [options]

        Options:
          --source-path <path>           Path to seed data directory (e.g., samples/Graph/Data)
          --connection-string <string>   PostgreSQL connection string
          --force                        Force re-import even if data exists
          --help                         Show this help
        """);
}
