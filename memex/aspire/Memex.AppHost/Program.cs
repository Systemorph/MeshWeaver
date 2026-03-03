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

    // Embedding configuration (Cohere embed-v4 via Azure Foundry)
    var embeddingEndpoint = builder.AddParameter("embedding-endpoint", secret: false);
    var embeddingKey = builder.AddParameter("embedding-key", secret: true);
    var embeddingModel = builder.AddParameter("embedding-model", secret: false);

    // Authentication provider parameters (Aspire prompts for values via dashboard/config)
    // Empty values = provider not enabled
    var microsoftClientId = builder.AddParameter("microsoft-client-id", secret: false);
    var microsoftClientSecret = builder.AddParameter("microsoft-client-secret", secret: true);
    // TODO: uncomment when ready to configure these providers
    // var googleClientId = builder.AddParameter("google-client-id", secret: false);
    // var googleClientSecret = builder.AddParameter("google-client-secret", secret: true);
    // var linkedinClientId = builder.AddParameter("linkedin-client-id", secret: false);
    // var linkedinClientSecret = builder.AddParameter("linkedin-client-secret", secret: true);
    // var appleClientId = builder.AddParameter("apple-client-id", secret: false);
    // var appleClientSecret = builder.AddParameter("apple-client-secret", secret: true);

    // Memex Distributed (co-hosted silo + web)
    builder
        .AddProject<Projects.Memex_Portal_Distributed>("memex-distributed")
        .WithExternalHttpEndpoints()
        .WithReference(orleans)
        .WithReference(postgresDb)
        .WithReference(storageBlobs)
        .WithEnvironment("Embedding__Endpoint", embeddingEndpoint)
        .WithEnvironment("Embedding__ApiKey", embeddingKey)
        .WithEnvironment("Embedding__Model", embeddingModel)
        // Authentication providers (read by AuthenticationBuilderExtensions)
        .WithEnvironment("Authentication__EnableDevLogin", "true")
        .WithEnvironment("Authentication__Microsoft__ClientId", microsoftClientId)
        .WithEnvironment("Authentication__Microsoft__ClientSecret", microsoftClientSecret)
        // TODO: uncomment when ready to configure these providers
        // .WithEnvironment("Authentication__Google__ClientId", googleClientId)
        // .WithEnvironment("Authentication__Google__ClientSecret", googleClientSecret)
        // .WithEnvironment("Authentication__LinkedIn__ClientId", linkedinClientId)
        // .WithEnvironment("Authentication__LinkedIn__ClientSecret", linkedinClientSecret)
        // .WithEnvironment("Authentication__Apple__ClientId", appleClientId)
        // .WithEnvironment("Authentication__Apple__ClientSecret", appleClientSecret)
        .WaitFor(storageBlobs)
        .WaitFor(orleansTables)
        .WaitFor(grainStateBlobs)
        .WaitFor(postgresDb);
}

if (useMonolith)
{
    // Memex Monolith (standalone, no Orleans)
    // Auth providers configured via appsettings.json Authentication section
    builder
        .AddProject<Projects.Memex_Portal_Monolith>("memex-monolith")
        .WithExternalHttpEndpoints();
}

var app = builder.Build();
app.Run();
