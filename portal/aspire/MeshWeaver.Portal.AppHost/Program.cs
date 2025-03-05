using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Application storage (for tables and blobs)
var appStorage = builder.AddAzureStorage("meshweaverblobs");
if (builder.Environment.IsDevelopment())
{
    appStorage = appStorage.RunAsEmulator(
        azurite =>
        {
            azurite.WithDataBindMount("../Azurite/Data")
                .WithExternalHttpEndpoints();
        });
}

var postgres = builder
    .AddPostgres("postgres")
    .WithPgAdmin()
    .WithDataVolume()
;
var meshweaverdb = postgres.AddDatabase("meshweaverdb");

// Add Application Insights
// Add Application Insights
var insights = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureApplicationInsights("myInsightsResource")
    : builder.AddConnectionString("myInsightsResource", "APPLICATIONINSIGHTS_CONNECTION_STRING");



var redis = builder.AddRedis("orleans-redis");
var orleans = builder.AddOrleans("mesh")
    .WithClustering(redis)
    .WithGrainStorage("address-registry", redis)
    .WithGrainStorage("mesh-catalog", appStorage.AddTables("mesh-catalog"))
    .WithGrainStorage("activity", appStorage.AddTables("activity"));

builder
    .AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
    .WithReference(orleans)
    .WithReference(meshweaverdb)
    .WithReference(insights)
    .WaitFor(redis)
    .WaitFor(meshweaverdb);

var frontend = builder
        .AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
        .WithExternalHttpEndpoints()
        .WithReference(orleans.AsClient())
        .WithReference(appStorage.AddBlobs("articles"))
        .WithReference(meshweaverdb)
        .WithReference(insights)
        .WaitFor(redis)
        .WaitFor(meshweaverdb)
    ;

// Then update your frontend configuration like this:
if (builder.ExecutionContext.IsPublishMode)
{
    var customDomain = builder.AddParameter("customDomain"); // Value provided at first deployment.
    var certificateName = builder.AddParameter("certificateName"); // Value provided at second and subsequent deployments.
    frontend = frontend
        .PublishAsAzureContainerApp((module, app) =>
        {
#pragma warning disable ASPIREACADOMAINS001 // Suppress warning about evaluation features
            app.ConfigureCustomDomain(customDomain, certificateName);
#pragma warning restore ASPIREACADOMAINS001 // Suppress warning about evaluation features
        });
}

var app = builder.Build();


app.Run();
