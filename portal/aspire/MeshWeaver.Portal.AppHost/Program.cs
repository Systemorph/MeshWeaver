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
    ? builder.AddAzureApplicationInsights("meshweaverinsights")
    : builder.AddConnectionString("meshweaverinsights", "APPLICATIONINSIGHTS_CONNECTION_STRING");



var redis = builder.AddRedis("orleans-redis")
    .WithDataVolume()  // Add persistent storage
    .WithPersistence(TimeSpan.FromMinutes(1), 10)  // Save data every minute if 10+ keys changed
    .WithAnnotation(new CommandLineArgsCallbackAnnotation(context =>
    {
        // Configure Redis with command line arguments
        context.Args.Add("--maxmemory-policy");
        context.Args.Add("allkeys-lru");
        context.Args.Add("--tcp-keepalive");
        context.Args.Add("60");
        return Task.CompletedTask;
    })); 


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

// Register all parameters upfront for both domains
var meshweaverDomain = builder.AddParameter("meshweaverDomain");
var meshweaverCertificate = builder.AddParameter("meshweaverCertificate");


// Then update your frontend configuration like this:
if (builder.ExecutionContext.IsPublishMode)
{
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
