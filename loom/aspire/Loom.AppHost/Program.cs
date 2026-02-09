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
    var appStorage = builder.AddAzureStorage("loomblobs");
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

    var orleans = builder.AddOrleans("loom-mesh")
        .WithClustering(orleansTables);

    // Cosmos DB for graph persistence
    var cosmos = builder.AddAzureCosmosDB("loomcosmos");
    if (builder.Environment.IsDevelopment())
    {
        cosmos = cosmos.RunAsEmulator();
    }
    var cosmosDb = cosmos.AddCosmosDatabase("loomdb");

    // Loom Orleans (co-hosted silo + web)
    builder
        .AddProject<Projects.Loom_Portal_Orleans>("loom-orleans")
        .WithExternalHttpEndpoints()
        .WithReference(orleans)
        .WithReference(cosmosDb)
        .WithReference(storageBlobs)
        .WaitFor(orleansTables)
        .WaitFor(cosmosDb);
}

if (useMonolith)
{
    // Loom Monolith (standalone, no Orleans)
    builder
        .AddProject<Projects.Loom_Portal_Monolith>("loom-monolith")
        .WithExternalHttpEndpoints();
}

var app = builder.Build();
app.Run();
