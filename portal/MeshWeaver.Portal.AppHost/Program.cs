var builder = DistributedApplication.CreateBuilder(args);
var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var redis = builder.AddRedis("redis");

var orleans = builder.AddOrleans("default")
    .WithClustering(redis)
    .WithGrainStorage("redis", redis)
    .WithGrainStorage("mesh-catalog", storage.AddTables("mesh-catalog"))
    .WithGrainStorage("activity", storage.AddTables("activity"))
    ;

builder.AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
       .WithReference(orleans)
       .WithReplicas(3);

builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithExternalHttpEndpoints()
    .WithReference(orleans.AsClient());

builder.Build().Run();
