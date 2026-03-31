using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Persistence;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

// Parse arguments
string? sourcePath = null;
string? connectionString = null;
string database = "memexdb";
bool force = false;
bool allowInsecureSsl = false;

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
        case "--database" when i + 1 < args.Length:
            database = args[++i];
            break;
        case "--force":
            force = true;
            break;
        case "--allow-insecure-ssl":
            allowInsecureSsl = true;
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

// Set up target (Cosmos DB) — configured with System.Text.Json serializer
var clientOptions = new CosmosClientOptions
{
    RequestTimeout = TimeSpan.FromSeconds(30),
    UseSystemTextJsonSerializerWithOptions = jsonOptions
};
if (allowInsecureSsl)
{
    clientOptions.HttpClientFactory = () => new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
    clientOptions.ConnectionMode = ConnectionMode.Gateway;
    clientOptions.LimitToEndpoint = true;
}
var cosmosClient = new CosmosClient(connectionString, clientOptions);
var cosmosOptions = new CosmosStorageOptions { DatabaseName = database };

Console.WriteLine($"Connecting to Cosmos DB at {connectionString[..connectionString.IndexOf(';')]}...");

// Ensure database and containers exist (idempotent — safe to call even if AppHost already created them)
var dbResponse = await cosmosClient.CreateDatabaseIfNotExistsAsync(database);
await dbResponse.Database.CreateContainerIfNotExistsAsync(
    new ContainerProperties(cosmosOptions.NodesContainerName, "/namespace") { DefaultTimeToLive = -1 });
await dbResponse.Database.CreateContainerIfNotExistsAsync(
    new ContainerProperties(cosmosOptions.PartitionsContainerName, "/partitionKey") { DefaultTimeToLive = -1 });
Console.WriteLine("Database and containers ready.");

var db = cosmosClient.GetDatabase(database);
var nodesContainer = db.GetContainer(cosmosOptions.NodesContainerName);
var partitionsContainer = db.GetContainer(cosmosOptions.PartitionsContainerName);
var target = new CosmosStorageAdapter(nodesContainer, partitionsContainer);

// Run import using shared helper
var result = await ImportHelper.RunImportAsync(source, target, logger, force, onProgress: (nodes, partitions, path) =>
{
    if (nodes % 25 == 0)
        Console.WriteLine($"  Progress: {nodes} nodes, {partitions} partitions (current: {path})");
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
        Usage: dotnet run --project tools/MeshWeaver.Tools.CosmosImport -- [options]

        Options:
          --source-path <path>           Path to seed data directory (e.g., samples/Graph/Data)
          --connection-string <string>   Cosmos DB connection string
          --database <name>              Database name (default: memexdb)
          --force                        Force re-import even if data exists
          --allow-insecure-ssl           Accept self-signed SSL certificates (for local emulator)
          --help                         Show this help
        """);
}
