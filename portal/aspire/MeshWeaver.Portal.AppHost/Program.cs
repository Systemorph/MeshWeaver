using Aspire.Hosting.Azure;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Application storage (for tables and blobs)
var appStorage = builder.AddAzureStorage("meshweaverblobs");
if (builder.Environment.IsDevelopment())
{
    appStorage = appStorage.RunAsEmulator(
        azurite =>
        {
            azurite.WithDataBindMount("../Azurite/Data");
        });

}

var postgres = builder
    .AddPostgres("postgres")
    .WithPgAdmin();

var postgresdb = postgres.AddDatabase("postgresdb");

var redis = builder.AddRedis("orleans-redis");
var orleans = builder.AddOrleans("mesh")
    .WithClustering(redis)
    .WithGrainStorage("address-registry", redis)
    .WithGrainStorage("mesh-catalog", appStorage.AddTables("mesh-catalog"))
    .WithGrainStorage("activity", appStorage.AddTables("activity"));

builder.AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
    .WithReference(orleans)
    .WithReplicas(1);

builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithExternalHttpEndpoints()
    .WithReference(orleans.AsClient())
    .WithReference(appStorage.AddBlobs("articles"))
    .WithReference(postgresdb);

builder.Build().Run();
