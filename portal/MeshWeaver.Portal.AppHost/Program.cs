using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
//var storage = builder.AddAzureStorage("storage").RunAsEmulator();

//var redis = builder.AddRedis("orleans-redis");

var cosmos = builder.AddAzureCosmosDB("cosmos");
var cosmosClustering = cosmos.AddDatabase("clustering");

if (builder.Environment.IsDevelopment())
    cosmos.RunAsEmulator();

// Add the Orleans resource to the Aspire DistributedApplication
// builder, then configure it with Redis for clustering.
// We do not explicitly include any grain storage, as this is 
// typically done lower level in the message hubs.
var orleans = builder.AddOrleans("default")
    .WithClustering(cosmosClustering)
    .WithGrainStorage("mesh-catalog", cosmos.AddDatabase("mesh-catalog"))
    .WithGrainStorage("routing", cosmos.AddDatabase("routing"))
    ;

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

builder.AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithExternalHttpEndpoints()
    .WithReference(orleans.AsClient());

builder.Build().Run();
