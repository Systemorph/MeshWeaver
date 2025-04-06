using Aspire.Hosting;
using Microsoft.Extensions.Hosting;

// Add this namespace for Azure resources

var builder = DistributedApplication.CreateBuilder(args);

// Application storage (for tables and blobs)
var appStorage = builder.AddAzureStorage("meshweaverblobs");
if (builder.Environment.IsDevelopment())
{
    appStorage = appStorage.RunAsEmulator(
        azurite =>
        {
            azurite.WithDataBindMount("../../Azurite/Data")
                .WithExternalHttpEndpoints();
        });
}

// Create Azure Table resources for Orleans clustering and storage
var orleansTables = appStorage.AddTables("orleans-clustering");

var orleans = builder.AddOrleans("mesh")
    .WithClustering(orleansTables);

// Add the EntraId parameters
var entraIdTenantId = builder.AddParameter("EntraIdTenantId");
var entraIdClientId = builder.AddParameter("EntraIdClientId");
var entraIdAdminGroupId = builder.AddParameter("PortalAdminGroup");

// PostgreSQL database setup - conditionally use containerized or Azure PostgreSQL
if (!builder.ExecutionContext.IsPublishMode)
{
    // Use containerized PostgreSQL for development
    var postgres = builder
        .AddPostgres("postgres")
        .WithPgAdmin()
        .WithDataVolume();

    var meshweaverdb = postgres.AddDatabase("meshweaverdb");

    // Add database migration service
    var migrationService = builder
        .AddProject<Projects.MeshWeaver_Portal_MigrationService>("db-migrations")
        .WithReference(meshweaverdb)
        .WaitFor(meshweaverdb);

    // Configure the silo to wait for database migrations to complete
    var silo = builder
        .AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
        .WithReference(orleans)
        .WithReference(meshweaverdb)
        .WaitForCompletion(migrationService)
        .WaitFor(orleansTables);

    // Configure the frontend to wait for database migrations to complete
    var frontend = builder
        .AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
        .WithExternalHttpEndpoints()
        .WithReference(orleans.AsClient())
        .WithReference(appStorage.AddBlobs("documentation"))
        .WithReference(appStorage.AddBlobs("reinsurance"))
        .WithEnvironment("EntraId__TenantId", entraIdTenantId)
        .WithEnvironment("EntraId__ClientId", entraIdClientId)
        .WithEnvironment("EntraId__RoleMappings__PortalAdmin", entraIdAdminGroupId)
        .WithReference(meshweaverdb)
        .WaitForCompletion(migrationService)
        .WaitFor(orleansTables);
}
else
{
    // For production, use Azure PostgreSQL with managed identity
    var azurePostgres = builder
        .AddAzurePostgresFlexibleServer("azure-postgres");


    var azureMeshweaverdb = azurePostgres.AddDatabase("meshweaverdb");

    // Add database migration service
    var migrationService = builder
        .AddProject<Projects.MeshWeaver_Portal_MigrationService>("db-migrations")
        .WithReference(azureMeshweaverdb)
        .WaitFor(azureMeshweaverdb);

    // Configure the silo to wait for database migrations to complete
    var silo = builder
        .AddProject<Projects.MeshWeaver_Portal_Orleans>("silo")
        .WithReplicas(1)
        .WithReference(orleans)
        .WithReference(azureMeshweaverdb)
        .WaitForCompletion(migrationService)
        .WaitFor(orleansTables);

    var googleTrackingId = builder.AddParameter("GoogleAnalyticsTrackingId");
        // Configure the frontend to wait for database migrations to complete
    var frontend = builder
        .AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
        .WithExternalHttpEndpoints()
        .WithReference(orleans.AsClient())
        .WithReference(appStorage.AddBlobs("documentation"))
        .WithReference(appStorage.AddBlobs("reinsurance"))
        .WithReference(azureMeshweaverdb)
        .WithEnvironment("EntraId__TenantId", entraIdTenantId)
        .WithEnvironment("EntraId__ClientId", entraIdClientId)
        .WithEnvironment("EntraId__RoleMappings__PortalAdmin", entraIdAdminGroupId)
        .WithEnvironment("GoogleAnalyticsTrackingId", googleTrackingId)
        .WaitForCompletion(migrationService)
        .WaitFor(orleansTables);

    // Add Application Insights
    //var insights = builder.AddAzureApplicationInsights("meshweaverinsights");
    //silo.WithReference(insights);
    //frontend.WithReference(insights);

    // Register all parameters upfront for both domains
    var meshweaverDomain = builder.AddParameter("meshweaverDomain");
    var meshweaverCertificate = builder.AddParameter("meshweaverCertificate");
    // Add Azure PostgreSQL connection string parameter for production

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
