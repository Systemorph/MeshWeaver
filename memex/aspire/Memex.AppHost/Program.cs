using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Mode: "monolith" (default) or "distributed"
// Pass as command line argument: dotnet run -- --mode distributed
var mode = builder.Configuration["mode"]?.ToLowerInvariant() ?? "monolith";
var useDistributed = mode == "distributed";
var useMonolith = mode == "monolith";

if (useDistributed)
{
    // Persistent storage for content (survives AppHost restarts)
    var contentStorage = builder.AddAzureStorage("memexblobs");
    if (builder.Environment.IsDevelopment())
    {
        contentStorage = contentStorage.RunAsEmulator(
            azurite => azurite
                .WithDataBindMount("../../Azurite/Data")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithExternalHttpEndpoints());
    }
    var storageBlobs = contentStorage.AddBlobs("storage");

    // Ephemeral storage for Orleans (fresh cluster on each restart to avoid stale silo entries)
    var orleansStorage = builder.AddAzureStorage("orleansstorage");
    if (builder.Environment.IsDevelopment())
    {
        orleansStorage = orleansStorage.RunAsEmulator();
    }
    var orleansTables = orleansStorage.AddTables("orleans-clustering");
    var grainStateBlobs = orleansStorage.AddBlobs("orleans-grain-state");

    var orleans = builder.AddOrleans("memex-mesh")
        .WithClustering(orleansTables)
        .WithGrainStorage("Default", grainStateBlobs);

    // PostgreSQL with pgvector for graph persistence
    var postgres = builder.AddPostgres("memex-postgres")
        .WithImage("pgvector/pgvector", "pg17")
        .WithDataVolume("memex-pgdata")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithPgAdmin(pgAdmin => pgAdmin.WithLifetime(ContainerLifetime.Persistent));
    var postgresDb = postgres.AddDatabase("meshweaver");

    // Azure Key Vault for data protection key encryption (optional — omit in dev for simplicity)
    // In production, set the "keyvault-key-uri" parameter to your Key Vault key URI
    // e.g., https://mykeyvault.vault.azure.net/keys/dataprotection
    var keyVaultKeyUri = builder.AddParameter("keyvault-key-uri", secret: false);

    // Embedding configuration (Cohere embed-v4 via Azure Foundry)
    var embeddingEndpoint = builder.AddParameter("embedding-endpoint", secret: false);
    var embeddingKey = builder.AddParameter("embedding-key", secret: true);
    var embeddingModel = builder.AddParameter("embedding-model", secret: false);

    // Memex Distributed (co-hosted silo + web)
    builder
        .AddProject<Projects.Memex_Portal_Distributed>("memex-distributed")
        .WithExternalHttpEndpoints()
        .WithReference(orleans)
        .WithReference(postgresDb)
        .WithReference(storageBlobs)
        .WithEnvironment("DataProtection__ConnectionName", "storage")
        .WithEnvironment("DataProtection__KeyVaultKeyUri", keyVaultKeyUri)
        .WithEnvironment("Embedding__Endpoint", embeddingEndpoint)
        .WithEnvironment("Embedding__ApiKey", embeddingKey)
        .WithEnvironment("Embedding__Model", embeddingModel)
        .WaitFor(storageBlobs)
        .WaitFor(orleansTables)
        .WaitFor(grainStateBlobs)
        .WaitFor(postgresDb);
}

if (useMonolith)
{
    // Memex Monolith (standalone, no Orleans)
    builder
        .AddProject<Projects.Memex_Portal_Monolith>("memex-monolith")
        .WithExternalHttpEndpoints();
}

var app = builder.Build();
app.Run();
