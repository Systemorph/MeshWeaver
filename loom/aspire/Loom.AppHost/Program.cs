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

    var orleans = builder.AddOrleans("loom-mesh")
        .WithClustering(orleansTables);

    // Loom Orleans (co-hosted silo + web)
    builder
        .AddProject<Projects.Loom_Portal_Orleans>("loom-orleans")
        .WithExternalHttpEndpoints()
        .WithReference(orleans)
        .WaitFor(orleansTables);
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
