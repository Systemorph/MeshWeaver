using Azure.Identity;
using Azure.Storage.Blobs;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using Microsoft.AspNetCore.DataProtection;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Messaging;
using Npgsql;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddServerSideBlazor().AddCircuitOptions(o => o.DetailedErrors = true);

// Log levels controlled via appsettings.Development.json

// Register Aspire-injected clients
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddKeyedAzureBlobServiceClient("storage");
builder.AddKeyedAzureBlobServiceClient("orleans-grain-state");

// Data protection: persist keys to Azure Blob Storage (shared across replicas)
var dpConfig = builder.Configuration.GetSection("DataProtection");
var containerName = dpConfig["ContainerName"] ?? "dataprotection";
var blobName = dpConfig["BlobName"] ?? "keys.xml";

builder.Services.AddDataProtection()
    .SetApplicationName("MemexPortal")
    .PersistKeysToAzureBlobStorage(sp =>
    {
        var blobServiceClient = sp.GetRequiredKeyedService<BlobServiceClient>("storage");
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        containerClient.CreateIfNotExists();
        return containerClient.GetBlobClient(blobName);
    });

// Register Aspire-injected PostgreSQL data source (with pgvector support)
// Single shared pool for all partition queries (schema-qualified SQL).
// Pool size must handle parallel fan-out across all schemas.
var connectionString = builder.Configuration.GetConnectionString("memex") ?? "";
if (connectionString.Contains("database.azure.com"))
    builder.AddAzureNpgsqlDataSource("memex",
        configureDataSourceBuilder: dsb =>
        {
            dsb.UseVector();
            dsb.ConnectionStringBuilder.MaxPoolSize = 50;
            dsb.ConnectionStringBuilder.ConnectionIdleLifetime = 30;
        });
else
    builder.AddNpgsqlDataSource("memex",
        configureDataSourceBuilder: dsb =>
        {
            dsb.UseVector();
            dsb.ConnectionStringBuilder.MaxPoolSize = 50;
            dsb.ConnectionStringBuilder.ConnectionIdleLifetime = 30;
        });

// Disable dev login in the distributed deployment
builder.Configuration["Authentication:EnableDevLogin"] = "false";

// Add web portal services
builder.ConfigureMemexServices();

// Register embedding provider if configured (Cohere embed-v4 via Azure Foundry)
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
builder.Services.AddAzureFoundryEmbeddings(embeddingOptions);

// Configure Orleans with Azure Table Storage (co-hosted silo + web)
var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = MemexDistributedConstants.ClusterId;
            opts.ServiceId = MemexDistributedConstants.ServiceId;
        })
    )
    .ConfigureServices(services => services
        .AddPartitionedPostgreSqlPersistence(
            configureDataSource: connectionString.Contains("database.azure.com")
                ? dsb =>
                {
                    var credential = new DefaultAzureCredential();
                    dsb.UsePeriodicPasswordProvider(async (_, ct) =>
                    {
                        var token = await credential.GetTokenAsync(
                            new Azure.Core.TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct);
                        return token.Token;
                    }, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(10));
                }
                : null))
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

var app = builder.Build();

app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();

