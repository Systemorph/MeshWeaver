using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Messaging;
using Npgsql;
using Orleans.Configuration;
using Pgvector.Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Register Aspire-injected clients
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddKeyedAzureBlobServiceClient("storage");
builder.AddKeyedAzureBlobServiceClient("orleans-grain-state");

// Register Aspire-injected PostgreSQL data source (with pgvector support)
builder.AddNpgsqlDataSource("meshweaver",
    configureDataSourceBuilder: dsb => dsb.UseVector());

// Add web portal services
builder.ConfigureMemexServices();

// Configure Orleans with Azure Table Storage (co-hosted silo + web)
var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = MemexDistributedConstants.ClusterId;
            opts.ServiceId = MemexDistributedConstants.ServiceId;
        })
    )
    .ConfigureServices(services => services
        .AddPostgreSqlStorageFactory())
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

var app = builder.Build();

// Initialize PostgreSQL schema and seed data
await app.Services.InitializePostgreSqlSchemaAsync();
await SeedDataAsync(app.Services);

app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();

static async Task SeedDataAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeding");

    // Find seed data path relative to the application
    var seedPath = FindSeedDataPath();
    if (seedPath == null)
    {
        logger.LogInformation("No seed data directory found, skipping import.");
        return;
    }

    var adapter = services.GetRequiredService<MeshWeaver.Mesh.Services.IStorageAdapter>();

    // Check if data already exists
    var exists = await adapter.ExistsAsync("MeshWeaver");
    if (exists)
    {
        logger.LogInformation("Database already contains data, skipping seed import.");
        return;
    }

    logger.LogInformation("Importing seed data from {Path}...", seedPath);

    var source = new FileSystemStorageAdapter(seedPath);
    var jsonOptions = StorageImporter.CreateFullImportOptions();
    var importer = new StorageImporter(source, adapter, logger);
    var result = await importer.ImportAsync(new StorageImportOptions
    {
        JsonOptions = jsonOptions,
        OnProgress = (nodes, partitions, path) =>
        {
            if (nodes % 50 == 0)
                logger.LogInformation("  Progress: {Nodes} nodes, {Partitions} partitions (current: {Path})",
                    nodes, partitions, path);
        }
    });

    logger.LogInformation("Seed import complete: {Nodes} nodes, {Partitions} partitions in {Elapsed:F1}s",
        result.NodesImported, result.PartitionsImported, result.Elapsed.TotalSeconds);
}

static string? FindSeedDataPath()
{
    // Walk up from the app directory to find samples/Graph/Data
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 10; i++)
    {
        var candidate = Path.Combine(dir, "samples", "Graph", "Data");
        if (Directory.Exists(candidate))
            return candidate;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    return null;
}
