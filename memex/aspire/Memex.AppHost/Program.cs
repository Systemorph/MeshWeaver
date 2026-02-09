using Aspire.Hosting;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Mode: "monolith" (default) or "orleans"
// Pass as command line argument: dotnet run -- --mode orleans
var mode = builder.Configuration["mode"]?.ToLowerInvariant() ?? "monolith";
var useOrleans = mode == "orleans";
var useMonolith = mode == "monolith";

if (useOrleans)
{
    // Application storage for Orleans clustering
    var appStorage = builder.AddAzureStorage("memexblobs");
    if (builder.Environment.IsDevelopment())
    {
        appStorage = appStorage.RunAsEmulator(
            azurite => azurite
                .WithDataBindMount("../../Azurite/Data")
                .WithExternalHttpEndpoints());
    }

    // Create Azure Table resources for Orleans clustering
    var orleansTables = appStorage.AddTables("orleans-clustering");

    // Azure Blob for content collection "storage"
    var storageBlobs = appStorage.AddBlobs("storage");

    var orleans = builder.AddOrleans("memex-mesh")
        .WithClustering(orleansTables);

    // Cosmos DB for graph persistence
    var cosmos = builder.AddAzureCosmosDB("memexcosmos");
    if (builder.Environment.IsDevelopment())
    {
        cosmos = cosmos.RunAsEmulator();
    }
    var cosmosDb = cosmos.AddCosmosDatabase("memexdb");

    // Memex Orleans (co-hosted silo + web)
    builder
        .AddProject<Projects.Memex_Portal_Orleans>("memex-orleans")
        .WithExternalHttpEndpoints()
        .WithReference(orleans)
        .WithReference(cosmosDb)
        .WithReference(storageBlobs)
        .WaitFor(orleansTables)
        .WaitFor(cosmosDb);
}

if (useMonolith)
{
    // Memex Monolith (standalone, no Orleans)
    builder
        .AddProject<Projects.Memex_Portal_Monolith>("memex-monolith")
        .WithExternalHttpEndpoints();
}

var app = builder.Build();
app.Run();
