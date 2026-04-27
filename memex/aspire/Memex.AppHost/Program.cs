using Azure.Provisioning.AppContainers;
using Azure.Provisioning.ApplicationInsights;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Mode: "local" (default), "test", "prod", "monolith"
// Pass as command line argument: dotnet run -- --mode test
//
// Mode matrix:
//   Mode        | PostgreSQL              | Blob Storage | Orleans    | Portal Name
//   ----------- | ----------------------- | ------------ | ---------- | -----------
//   local       | Docker pgvector (memex) | Emulated     | Emulated   | memex-local
//   test        | Azure (memex-test)      | Azure        | Azure      | memex-test
//   prod        | Azure (memex)           | Azure        | Azure      | memex-prod
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
//   Parameters:microsoft-tenant-id

var mode = builder.Configuration["mode"]?.ToLowerInvariant() ?? "local";

if (mode == "monolith")
{
    // Standalone portal without Orleans or external infrastructure
    builder
        .AddProject<Projects.Memex_Portal_Monolith>("memex-monolith")
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithExternalHttpEndpoints();
    builder.Build().Run();
    return;
}

// --- Shared Parameters (linked to GitHub secrets; locally via `dotnet user-secrets`) ---

// LLM API key (single Azure Foundry key for both Anthropic and OpenAI endpoints)
var azureFoundryKey = builder.AddParameter("azure-foundry-key", secret: true);

// Authentication (Microsoft is required; Google is optional for local)
var microsoftClientId = builder.AddParameter("microsoft-client-id", secret: false);
var microsoftClientSecret = builder.AddParameter("microsoft-client-secret", secret: true);
var microsoftTenantId = builder.AddParameter("microsoft-tenant-id", secret: false);

// Embedding, Google auth, and custom domain (non-secret optional — ACA accepts empty env vars)
var embeddingEndpoint = builder.AddParameter("embedding-endpoint", value: "", secret: false);
var embeddingModel = builder.AddParameter("embedding-model", value: "", secret: false);
var googleClientId = builder.AddParameter("google-client-id", value: "", secret: false);
var customDomain = builder.AddParameter("custom-domain", value: "", secret: false);
var certificateName = builder.AddParameter("certificate-name", value: "", secret: false);

// Optional secrets/params: ACA rejects secrets with empty values; ConfigureCustomDomain
// rejects empty hostnames. Read actual config values to guard optional registrations.
var embeddingKeyValue = builder.Configuration["Parameters:embedding-key"] ?? "";
var googleClientSecretValue = builder.Configuration["Parameters:google-client-secret"] ?? "";
var customDomainValue = builder.Configuration["Parameters:custom-domain"] ?? "";
IResourceBuilder<ParameterResource>? embeddingKey = string.IsNullOrEmpty(embeddingKeyValue)
    ? null : builder.AddParameter("embedding-key", secret: true);
IResourceBuilder<ParameterResource>? googleClientSecret = string.IsNullOrEmpty(googleClientSecretValue)
    ? null : builder.AddParameter("google-client-secret", secret: true);

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

// --- Application Insights (skipped in pure local mode — no Azure subscription needed) ---
var appInsights = useLocalDb
    ? null
    : builder.AddAzureApplicationInsights("appinsights")
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

if (appInsights is not null)
{
    dbMigration.WithReference(appInsights).WaitFor(appInsights);
}

// --- Portal (co-hosted Orleans silo + web) ---
var portal = builder
    .AddProject<Projects.Memex_Portal_Distributed>(isDeployed ? $"memex-{mode}" : "memex-local")
    .WithExternalHttpEndpoints()
    .WithReference(orleans)
    // Local modes need Development environment for static web assets (_framework, _content)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", isDeployed ? "Production" : "Development")
    // Embedding
    .WithEnvironment("Embedding__Endpoint", embeddingEndpoint)
    .WithEnvironment("Embedding__Model", embeddingModel)
    // LLM: Anthropic (Azure Foundry Claude)
    .WithEnvironment("Anthropic__Endpoint", "https://s-meshweaver.services.ai.azure.com/anthropic/")
    .WithEnvironment("Anthropic__ApiKey", azureFoundryKey)
    .WithEnvironment("Anthropic__Models__0", "claude-sonnet-4-6")
    .WithEnvironment("Anthropic__Models__1", "claude-opus-4-7")
    .WithEnvironment("Anthropic__Models__2", "claude-haiku-4-5")
    .WithEnvironment("Anthropic__Order", "1")
    // Model tiers: map agent tiers to concrete models
    .WithEnvironment("ModelTier__Heavy", "claude-opus-4-7")
    .WithEnvironment("ModelTier__Standard", "claude-sonnet-4-6")
    .WithEnvironment("ModelTier__Light", "claude-haiku-4-5")
    // LLM: Azure OpenAI
    .WithEnvironment("AzureOpenAIS__Endpoint", "https://s-meshweaver.cognitiveservices.azure.com")
    .WithEnvironment("AzureOpenAIS__ApiKey", azureFoundryKey)
    .WithEnvironment("AzureOpenAIS__Models__0", "gpt-5-mini")
    .WithEnvironment("AzureOpenAIS__Models__1", "gpt-5.4")
    .WithEnvironment("AzureOpenAIS__Order", "2")
    // LLM: Azure AI Foundry (multi-model inference endpoint)
    .WithEnvironment("AzureAIS__Endpoint", "https://fy-meshweaver3-dev-swc-001.services.ai.azure.com/models")
    .WithEnvironment("AzureAIS__ApiKey", azureFoundryKey)
    .WithEnvironment("AzureAIS__Models__0", "gpt-5.4")
    .WithEnvironment("AzureAIS__Models__1", "gpt-5.3-codex")
    .WithEnvironment("AzureAIS__Models__2", "Mistral-Large-3")
    .WithEnvironment("AzureAIS__Models__3", "DeepSeek-V3.2")
    .WithEnvironment("AzureAIS__Order", "0")
    // Authentication
    .WithEnvironment("Authentication__EnableDevLogin", mode != "prod" ? "true" : "false")
    .WithEnvironment("Authentication__Microsoft__ClientId", microsoftClientId)
    .WithEnvironment("Authentication__Microsoft__ClientSecret", microsoftClientSecret)
    .WithEnvironment("Authentication__Microsoft__TenantId", microsoftTenantId)
    .WithEnvironment("Authentication__Google__ClientId", googleClientId)
    // Wait for dependencies
    .WaitFor(orleansTables)
    .WaitForCompletion(dbMigration)
    // ACA deployment: sticky sessions (Blazor Server) + custom domain + resources
    .PublishAsAzureContainerApp((module, app) =>
    {
        app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;
        if (!string.IsNullOrEmpty(customDomainValue))
            app.ConfigureCustomDomain(customDomain, certificateName);

        // Scale: min 2 replicas (Orleans needs ≥2 for resilience), max 6 under load.
        // Each replica: 2 vCPU / 4Gi (50% of Consumption tier max 4 vCPU / 8Gi).
        app.Template.Scale.MinReplicas = 2;
        app.Template.Scale.MaxReplicas = 6;
    });

// Optional secrets: only add as env vars when configured (ACA rejects empty secrets)
if (embeddingKey is not null)
    portal.WithEnvironment("Embedding__ApiKey", embeddingKey);
if (googleClientSecret is not null)
    portal.WithEnvironment("Authentication__Google__ClientSecret", googleClientSecret);

if (appInsights is not null)
    portal = portal.WithReference(appInsights);

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
