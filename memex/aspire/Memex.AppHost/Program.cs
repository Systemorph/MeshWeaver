using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Mode: "local" (default), "local-test", "local-prod", "test", "prod", "monolith"
// Pass as command line argument: dotnet run -- --mode local-test
//
// Mode matrix:
//   Mode        | PostgreSQL              | Blob Storage | Orleans    | Portal Name
//   ----------- | ----------------------- | ------------ | ---------- | -----------
//   local       | Docker pgvector         | Emulated     | Emulated   | memex-local
//   local-test  | Azure (meshweaver_test) | Emulated     | Emulated   | memex-local
//   local-prod  | Azure (meshweaver_prod) | Emulated     | Emulated   | memex-local
//   test        | Azure (meshweaver_test) | Azure         | Azure      | memex-test
//   prod        | Azure (meshweaver_prod) | Azure         | Azure      | memex-prod
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
//   ConnectionStrings:meshweaver  (Aspire uses this if set, bypassing provisioning)

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

// --- Infrastructure axes ---
var isDeployed = mode is "test" or "prod";
var useLocalDb = mode == "local";

// --- Azure Blob Storage ---
var contentStorage = builder.AddAzureStorage("memexblobs");
if (isDeployed)
{
    contentStorage = contentStorage.ConfigureInfrastructure(infra =>
    {
        var storageAccount = infra.GetProvisionableResources()
            .OfType<Azure.Provisioning.Storage.StorageAccount>()
            .Single();
        storageAccount.Location = new Azure.Core.AzureLocation("swedencentral");
    });
}
else if (builder.Environment.IsDevelopment())
{
    contentStorage = contentStorage.RunAsEmulator(
        azurite => azurite
            .WithDataBindMount("../../Azurite/Data")
            .WithLifetime(ContainerLifetime.Persistent)
            .WithExternalHttpEndpoints());
}
var storageBlobs = contentStorage.AddBlobs("storage");

// --- Orleans (ephemeral, fresh cluster on each restart) ---
var orleansStorage = builder.AddAzureStorage("orleansstorage");
if (!isDeployed && builder.Environment.IsDevelopment())
{
    orleansStorage = orleansStorage.RunAsEmulator();
}
var orleansTables = orleansStorage.AddTables("orleans-clustering");
var grainStateBlobs = orleansStorage.AddBlobs("orleans-grain-state");

var orleans = builder.AddOrleans("memex-mesh")
    .WithClustering(orleansTables)
    .WithGrainStorage("Default", grainStateBlobs);

// --- Database Migration ---
var dbMigration = builder
    .AddProject<Projects.Memex_Database_Migration>("db-migration")
    .WithEnvironment("Embedding__Model", embeddingModel);

// --- Portal (co-hosted Orleans silo + web) ---
var portal = builder
    .AddProject<Projects.Memex_Portal_Distributed>(isDeployed ? $"memex-{mode}" : "memex-local")
    .WithExternalHttpEndpoints()
    .WithReference(orleans)
    .WithReference(storageBlobs)
    // Embedding
    .WithEnvironment("Embedding__Endpoint", embeddingEndpoint)
    .WithEnvironment("Embedding__ApiKey", embeddingKey)
    .WithEnvironment("Embedding__Model", embeddingModel)
    // LLM: Anthropic (Azure Foundry Claude)
    .WithEnvironment("Anthropic__Endpoint", "https://s-meshweaver.services.ai.azure.com/anthropic/")
    .WithEnvironment("Anthropic__ApiKey", azureFoundryKey)
    .WithEnvironment("Anthropic__Models__0", "claude-haiku-4-5")
    .WithEnvironment("Anthropic__Models__1", "claude-sonnet-4-5")
    .WithEnvironment("Anthropic__Models__2", "claude-opus-4-5")
    // LLM: Azure OpenAI
    .WithEnvironment("AzureOpenAIS__Endpoint", "https://s-meshweaver.cognitiveservices.azure.com")
    .WithEnvironment("AzureOpenAIS__ApiKey", azureFoundryKey)
    .WithEnvironment("AzureOpenAIS__Models__0", "gpt-5-mini")
    .WithEnvironment("AzureOpenAIS__Models__1", "gpt-5.4")
    // Authentication
    .WithEnvironment("Authentication__EnableDevLogin", mode != "prod" ? "true" : "false")
    .WithEnvironment("Authentication__Microsoft__ClientId", microsoftClientId)
    .WithEnvironment("Authentication__Microsoft__ClientSecret", microsoftClientSecret)
    // Wait for dependencies
    .WaitFor(storageBlobs)
    .WaitFor(orleansTables)
    .WaitFor(grainStateBlobs)
    .WaitForCompletion(dbMigration);

// --- PostgreSQL ---
if (useLocalDb)
{
    // Local Docker pgvector container
    var postgres = builder.AddPostgres("memex-postgres")
        .WithImage("pgvector/pgvector", "pg17")
        .WithDataVolume("memex-pgdata")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithPgAdmin(pgAdmin => pgAdmin.WithLifetime(ContainerLifetime.Persistent));
    var db = postgres.AddDatabase("meshweaver");

    dbMigration.WithReference(db).WaitFor(db);
    portal.WithReference(db).WaitFor(db);
}
else
{
    // Azure PostgreSQL Flexible Server in Sweden Central (one server, db name per environment)
    var postgres = builder.AddAzurePostgresFlexibleServer("memex-postgres")
        .ConfigureInfrastructure(infra =>
        {
            var server = infra.GetProvisionableResources()
                .OfType<Azure.Provisioning.PostgreSql.PostgreSqlFlexibleServer>()
                .Single();
            server.Location = new Azure.Core.AzureLocation("swedencentral");
        });
    var dbName = mode is "local-test" or "test" ? "meshweaver_test" : "meshweaver_prod";
    var db = postgres.AddDatabase("meshweaver", databaseName: dbName);

    dbMigration.WithReference(db).WaitFor(db);
    portal.WithReference(db).WaitFor(db);
}

var app = builder.Build();
app.Run();
