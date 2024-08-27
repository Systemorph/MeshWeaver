var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("hubs-redis");



// Add the Orleans resource to the Aspire DistributedApplication
// builder, then configure it with Redis for clustering.
// We do not explicitly include any grain storage, as this is 
// typically done lower level in the message hubs.
var orleans = builder.AddOrleans("default")
    .WithClustering(redis)
    .WithGrainStorage("hubs", redis);

// Add our server project and reference your 'orleans' resource from it.
// it can join the Orleans cluster as a service.
// This implicitly add references to the required resources.
// In this case, that is the 'clusteringTable' resource declared earlier.
builder.AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
       .WithReference(orleans)
       .WithReplicas(3);

//var apiService = builder
//    .AddProject<Projects.MeshWeaver_Portal_Backend>("backend")
//    .WithReference(orleans);

//builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
//    .WithExternalHttpEndpoints()
//    .WithReference(redis)
//    .WithReference(orleans)
//    .WithReference(apiService);

builder.Build().Run();
