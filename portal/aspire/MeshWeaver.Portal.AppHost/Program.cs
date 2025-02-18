using Aspire.Hosting.Azure;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Application storage (for tables and blobs)
var appStorage = builder.AddAzureStorage("meshweaverblobs");
if (builder.Environment.IsDevelopment())
{
    appStorage.RunAsEmulator();
}

var postgres = builder
    .AddPostgres("postgres")
    .WithPgAdmin();

var postgresdb = postgres.AddDatabase("postgresdb");

// File share storage (for blog content)
//var fileStorage = builder.AddAzureStorage("meshweaverfiles");
//if (builder.Environment.IsDevelopment())
//{
//    fileStorage.RunAsEmulator();
//}
//var fileShare = fileStorage.WithBindMount<AzureStorageResource>("//smb-server/share", "/mnt/smb-share");
var redis = builder.AddRedis("orleans-redis");
var addressRegistry = builder.AddRedis("address-registry");
var orleans = builder.AddOrleans("mesh")
    .WithClustering(redis)
    .WithGrainStorage(addressRegistry)
    .WithGrainStorage("mesh-catalog", appStorage.AddTables("mesh-catalog"))
    .WithGrainStorage("activity", appStorage.AddTables("activity"));

builder.AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
    .WithReference(orleans)
    .WithReplicas(1);

builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithExternalHttpEndpoints()
    .WithReference(orleans.AsClient())
    .WithReference(postgresdb);

builder.Build().Run();
