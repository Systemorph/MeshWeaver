using Aspire.Hosting;
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


// Create Azure Table resources for Orleans clustering and storage
var orleansTables = appStorage.AddTables("orleans-clustering");
var addressRegistryTables = appStorage.AddTables("address-registry");
var meshCatalogTables = appStorage.AddTables("mesh-catalog");
var activityTables = appStorage.AddTables("activity");

var orleans = builder.AddOrleans("mesh")
    .WithClustering(orleansTables)
    .WithGrainStorage("address-registry", addressRegistryTables)
    .WithGrainStorage("mesh-catalog", meshCatalogTables)
    .WithGrainStorage("activity", activityTables);

var silo = builder
    .AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
    .WithReference(orleans)
    .WithReference(meshweaverdb)
    .WaitFor(meshweaverdb)
    .WaitFor(orleansTables)
    .WaitFor(addressRegistryTables)
    .WaitFor(meshCatalogTables)
    .WaitFor(activityTables);

var frontend = builder
        .AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
        .WithExternalHttpEndpoints()
        .WithReference(orleans.AsClient())
        .WithReference(appStorage.AddBlobs("articles"))
        .WithReference(meshweaverdb)
        .WaitFor(meshweaverdb)
        .WaitFor(orleansTables)
    ;

// Then update your frontend configuration like this:
if (builder.ExecutionContext.IsPublishMode)
{
    // Add Application Insights
    var insights = builder.ExecutionContext.IsPublishMode
        ? builder.AddAzureApplicationInsights("meshweaverinsights")
        : builder.AddConnectionString("meshweaverinsights", "APPLICATIONINSIGHTS_CONNECTION_STRING");

    silo.WithReference(insights);
    frontend.WithReference(insights);
    // Register all parameters upfront for both domains
    var meshweaverDomain = builder.AddParameter("meshweaverDomain");
    var meshweaverCertificate = builder.AddParameter("meshweaverCertificate");
    frontend
        .PublishAsAzureContainerApp((module, app) =>
        {
#pragma warning disable ASPIREACADOMAINS001 // Suppress warning about evaluation features
            app.ConfigureCustomDomain(meshweaverDomain, meshweaverCertificate);
#pragma warning restore ASPIREACADOMAINS001 // Suppress warning about evaluation features
        });
}


var app = builder.Build();

app.Run();
