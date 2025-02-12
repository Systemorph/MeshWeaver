var builder = DistributedApplication.CreateBuilder(args);
var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var redis = builder.AddRedis("orleans-redis");
var addressRegistry = builder.AddRedis("address-registry");
var orleans = builder.AddOrleans("mesh")
    .WithClustering(redis)
    .WithGrainStorage(addressRegistry)
    .WithGrainStorage("mesh-catalog", storage.AddTables("mesh-catalog"))
    .WithGrainStorage("activity", storage.AddTables("activity"))
    ;

builder.AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
       .WithReference(orleans)
       .WithReplicas(1);

builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithExternalHttpEndpoints()
    .WithReference(orleans.AsClient());

builder.Build().Run();
