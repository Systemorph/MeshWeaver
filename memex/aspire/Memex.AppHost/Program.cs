using Aspire.Hosting;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Mode: "monolith" (default) or "distributed"
// Pass as command line argument: dotnet run -- --mode distributed
var mode = builder.Configuration["mode"]?.ToLowerInvariant() ?? "monolith";
var useDistributed = mode == "distributed";
var useMonolith = mode == "monolith";

if (useDistributed)
{
    // Persistent storage for content (survives AppHost restarts)
    var contentStorage = builder.AddAzureStorage("memexblobs");
    if (builder.Environment.IsDevelopment())
    {
        contentStorage = contentStorage.RunAsEmulator(
            azurite => azurite
                .WithDataBindMount("../../Azurite/Data")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithExternalHttpEndpoints());
    }
    var storageBlobs = contentStorage.AddBlobs("storage");

    // Ephemeral storage for Orleans (fresh clustering table on each restart)
    var orleansStorage = builder.AddAzureStorage("orleansstorage");
    if (builder.Environment.IsDevelopment())
    {
        orleansStorage = orleansStorage.RunAsEmulator();
    }
    var orleansTables = orleansStorage.AddTables("orleans-clustering");
    var grainStateBlobs = orleansStorage.AddBlobs("orleans-grain-state");

    var orleans = builder.AddOrleans("memex-mesh")
        .WithClustering(orleansTables)
        .WithGrainStorage("Default", grainStateBlobs);

    // Cosmos DB for graph persistence (persistent container with data volume)
    var cosmos = builder.AddAzureCosmosDB("memexcosmos");
    if (builder.Environment.IsDevelopment())
    {
        cosmos = cosmos.RunAsEmulator(emulator => emulator
            .WithDataVolume()
            .WithLifetime(ContainerLifetime.Persistent));
    }
    var cosmosDb = cosmos.AddCosmosDatabase("memexdb");

    // Memex Distributed (co-hosted silo + web)
    builder
        .AddProject<Projects.Memex_Portal_Distributed>("memex-distributed")
        .WithExternalHttpEndpoints()
        .WithReference(orleans)
        .WithReference(cosmosDb)
        .WithReference(storageBlobs)
        .WaitFor(orleansTables)
        .WaitFor(grainStateBlobs)
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
