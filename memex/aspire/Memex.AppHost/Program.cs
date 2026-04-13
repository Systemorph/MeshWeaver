using Azure.Provisioning.AppContainers;
using Azure.Provisioning.ApplicationInsights;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Mode: "local" (default), "local-test", "local-prod", "test", "prod", "monolith"
// Pass as command line argument: dotnet run -- --mode local-test
//
// Mode matrix:
//   Mode        | PostgreSQL              | Blob Storage | Orleans    | Portal Name
//   ----------- | ----------------------- | ------------ | ---------- | -----------
//   local       | Docker pgvector (memex)      | Emulated     | Emulated   | memex-local
//   local-test  | Azure (memex-test)           | Azure (meshweavermemextest) | Emulated   | memex-local
//   local-prod  | Azure (memex)                | Azure (meshweavermemex)     | Emulated   | memex-local
//   test        | Azure (memex-test)           | Azure (meshweavermemextest) | Azure      | memex-test
//   prod        | Azure (memex)                | Azure (meshweavermemex)     | Azure      | memex-prod
//   monolith    | FileSystem (standalone) | —            | —          | memex-monolith
//
// Secrets: set locally via `dotnet user-secrets`, in CI/CD via GitHub secrets.
// UserSecretsId: memex-apphost (see Memex.AppHost.csproj)
//
// Required user-secrets for distributed modes:
//   Parameters:azure-foundry-key
//   Parameters:embedding-endpoint
//   Parameters:embedding-key
//   Parameters:embedding-model
//   Parameters:microsoft-client-id
//   Parameters:microsoft-client-secret
//
// For local-test/local-prod, also set the connection string to the Azure PostgreSQL:
//   ConnectionStrings:memex  (Azure PostgreSQL, bypassing provisioning)
// Blob Storage uses RunAsExisting with Azure Identity (az login) — no secrets needed.

var mode = builder.Configuration["mode"]?.ToLowerInvariant() ?? "local";

if (mode == "monolith")
{
    // Standalone portal without Orleans or external infrastructure
    builder
        .AddProject<Projects.Memex_Portal_Monolith>("memex-monolith")
        .WithExternalHttpEndpoints();
    builder.Build().Run();
    return;
}

// --- Shared Parameters (linked to GitHub secrets; locally via `dotnet user-secrets`) ---

// LLM API key (single Azure Foundry key for both Anthropic and OpenAI endpoints)
var azureFoundryKey = builder.AddParameter("azure-foundry-key", secret: true);

// Embedding configuration
var embeddingEndpoint = builder.AddParameter("embedding-endpoint", secret: false);
var embeddingKey = builder.AddParameter("embedding-key", secret: true);
var embeddingModel = builder.AddParameter("embedding-model", secret: false);

// Authentication
var microsoftClientId = builder.AddParameter("microsoft-client-id", secret: false);
var microsoftClientSecret = builder.AddParameter("microsoft-client-secret", secret: true);
var googleClientId = builder.AddParameter("google-client-id", secret: false);
var googleClientSecret = builder.AddParameter("google-client-secret", secret: true);

// --- Custom domain (for deployed modes) ---
var customDomain = builder.AddParameter("custom-domain", secret: false);
var certificateName = builder.AddParameter("certificate-name", secret: false);

// --- Infrastructure axes ---
var isDeployed = mode is "test" or "prod";
var useLocalDb = mode == "local";

// --- ACA Environment (Sweden Central) ---
if (isDeployed)
{
    builder.AddAzureContainerAppEnvironment("memex-aca")
        .ConfigureInfrastructure(infra =>
        {
            var env = infra.GetProvisionableResources()
                .OfType<Azure.Provisioning.AppContainers.ContainerAppManagedEnvironment>()
                .Single();
            env.Location = new Azure.Core.AzureLocation("swedencentral");
        });
}

// --- Orleans (ephemeral, fresh cluster on each restart) ---
var orleansStorage = builder.AddAzureStorage("orleansstorage");
if (!isDeployed && builder.Environment.IsDevelopment())
{
    orleansStorage = orleansStorage.RunAsEmulator();
}
else if (isDeployed)
{
    orleansStorage = orleansStorage.ConfigureInfrastructure(infra =>
    {
        var storageAccount = infra.GetProvisionableResources()
            .OfType<Azure.Provisioning.Storage.StorageAccount>()
            .Single();
        storageAccount.Location = new Azure.Core.AzureLocation("swedencentral");
    });
}
var orleansTables = orleansStorage.AddTables("orleans-clustering");

var orleans = builder.AddOrleans("memex-mesh")
    .WithClustering(orleansTables);

// --- Application Insights ---
var appInsights = builder.AddAzureApplicationInsights("appinsights")
    .ConfigureInfrastructure(infra =>
    {
        var component = infra.GetProvisionableResources()
            .OfType<Azure.Provisioning.ApplicationInsights.ApplicationInsightsComponent>()
            .Single();
        component.Location = new Azure.Core.AzureLocation("swedencentral");
    });

// --- Database Migration ---
var dbMigration = builder
    .AddProject<Projects.Memex_Database_Migration>("db-migration")
    .WithEnvironment("Embedding__Model", embeddingModel);

if (!useLocalDb)
{
    dbMigration.WithReference(appInsights).WaitFor(appInsights);
}

// --- Portal (co-hosted Orleans silo + web) ---
var portal = builder
    .AddProject<Projects.Memex_Portal_Distributed>(isDeployed ? $"memex-{mode}" : "memex-local")
    .WithExternalHttpEndpoints()
    .WithReference(orleans)
    .WithReference(appInsights)
    // Embedding
    .WithEnvironment("Embedding__Endpoint", embeddingEndpoint)
    .WithEnvironment("Embedding__ApiKey", embeddingKey)
    .WithEnvironment("Embedding__Model", embeddingModel)
    // LLM: Anthropic (Azure Foundry Claude)
    .WithEnvironment("Anthropic__Endpoint", "https://s-meshweaver.services.ai.azure.com/anthropic/")
    .WithEnvironment("Anthropic__ApiKey", azureFoundryKey)
    .WithEnvironment("Anthropic__Models__0", "claude-sonnet-4-6")
    .WithEnvironment("Anthropic__Models__1", "claude-opus-4-6")
    .WithEnvironment("Anthropic__Models__2", "claude-haiku-4-5")
    // Model tiers: map agent tiers to concrete models
    .WithEnvironment("ModelTier__Heavy", "claude-opus-4-6")
    .WithEnvironment("ModelTier__Standard", "claude-sonnet-4-6")
    .WithEnvironment("ModelTier__Light", "claude-haiku-4-5")
    // LLM: Azure OpenAI
    .WithEnvironment("AzureOpenAIS__Endpoint", "https://s-meshweaver.cognitiveservices.azure.com")
    .WithEnvironment("AzureOpenAIS__ApiKey", azureFoundryKey)
    .WithEnvironment("AzureOpenAIS__Models__0", "gpt-5-mini")
    .WithEnvironment("AzureOpenAIS__Models__1", "gpt-5.4")
    // Authentication
    .WithEnvironment("Authentication__EnableDevLogin", mode != "prod" ? "true" : "false")
    .WithEnvironment("Authentication__Microsoft__ClientId", microsoftClientId)
    .WithEnvironment("Authentication__Microsoft__ClientSecret", microsoftClientSecret)
    .WithEnvironment("Authentication__Google__ClientId", googleClientId)
    .WithEnvironment("Authentication__Google__ClientSecret", googleClientSecret)
    // Wait for dependencies
    .WaitFor(orleansTables)
    .WaitForCompletion(dbMigration)
    // ACA deployment: sticky sessions (Blazor Server) + custom domain + resources
    .PublishAsAzureContainerApp((module, app) =>
    {
        app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;
        app.ConfigureCustomDomain(customDomain, certificateName);

        // Scale: min 2 replicas (Orleans needs ≥2 for resilience), max 6 under load.
        // Each replica: 2 vCPU / 4Gi (50% of Consumption tier max 4 vCPU / 8Gi).
        app.Template.Scale.MinReplicas = 2;
        app.Template.Scale.MaxReplicas = 6;
    });

// --- Azure Blob Storage ---
if (useLocalDb)
{
    // Local emulated storage
    var contentStorage = builder.AddAzureStorage("memexblobs")
        .RunAsEmulator(
            azurite => azurite
                .WithDataBindMount("../../Azurite/Data")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithExternalHttpEndpoints());
    var storageBlobs = contentStorage.AddBlobs("storage");
    portal.WithReference(storageBlobs).WaitFor(storageBlobs);
}
else if (mode is "local-test" or "local-prod")
{
    // Connect to existing Azure Blob Storage via Azure Identity (az login, no secrets needed)
    var storageName = mode is "local-test" ? "meshweavermemextest" : "meshweavermemex";
    var contentStorage = builder.AddAzureStorage("memexblobs")
        .RunAsExisting(storageName, null);
    var storageBlobs = contentStorage.AddBlobs("storage");
    portal.WithReference(storageBlobs);
}
else
{
    // Deployed modes: provision Azure Blob Storage in Sweden Central
    var contentStorage = builder.AddAzureStorage("memexblobs")
        .ConfigureInfrastructure(infra =>
        {
            var storageAccount = infra.GetProvisionableResources()
                .OfType<Azure.Provisioning.Storage.StorageAccount>()
                .Single();
            storageAccount.Location = new Azure.Core.AzureLocation("swedencentral");
        });
    var storageBlobs = contentStorage.AddBlobs("storage");
    portal.WithReference(storageBlobs).WaitFor(storageBlobs);
}

// --- PostgreSQL ---
if (useLocalDb)
{
    // Local Docker pgvector container
    var postgres = builder.AddPostgres("memex-postgres")
        .WithImage("pgvector/pgvector", "pg17")
        .WithDataVolume("memex-pgdata")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithPgAdmin(pgAdmin => pgAdmin.WithLifetime(ContainerLifetime.Persistent));
    var db = postgres.AddDatabase("memex");

    dbMigration.WithReference(db).WaitFor(db);
    portal.WithReference(db).WaitFor(db);
}
else if (mode is "local-test" or "local-prod")
{
    // Use pre-configured connection string (set via dotnet user-secrets)
    // to connect to existing Azure PostgreSQL without Aspire provisioning.
    var db = builder.AddConnectionString("memex");
    dbMigration.WithReference(db);
    portal.WithReference(db);
}
else
{
    // Deployed modes: provision Azure PostgreSQL Flexible Server in Sweden Central
    var postgres = builder.AddAzurePostgresFlexibleServer("memex-postgres")
        .ConfigureInfrastructure(infra =>
        {
            var server = infra.GetProvisionableResources()
                .OfType<Azure.Provisioning.PostgreSql.PostgreSqlFlexibleServer>()
                .Single();
            server.Location = new Azure.Core.AzureLocation("swedencentral");
        });
    var dbName = mode is "test" ? "memex-test" : "memex";
    var db = postgres.AddDatabase("memex", databaseName: dbName);

    dbMigration.WithReference(db).WaitFor(db);
    portal.WithReference(db).WaitFor(db);
}

var app = builder.Build();
app.Run();
