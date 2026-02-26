using Azure.Identity;
using Azure.Storage.Blobs;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Register Aspire-injected clients
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddKeyedAzureBlobServiceClient("storage");
builder.AddKeyedAzureBlobServiceClient("orleans-grain-state");

// Register Aspire-injected PostgreSQL data source (with pgvector support)
builder.AddNpgsqlDataSource("meshweaver",
    configureDataSourceBuilder: dsb => dsb.UseVector());

// Add web portal services
builder.ConfigureMemexServices();

// Data protection: persist keys to Azure Blob Storage (shared across instances)
// and optionally encrypt with Azure Key Vault
var dpSection = builder.Configuration.GetSection("DataProtection");
var dpConnectionString = dpSection["ConnectionString"]
                         ?? builder.Configuration.GetConnectionString(dpSection["ConnectionName"] ?? "storage");
if (!string.IsNullOrEmpty(dpConnectionString))
{
    var containerName = dpSection["ContainerName"] ?? "dataprotection";
    var blobName = dpSection["BlobName"] ?? "keys.xml";

    var dpBuilder = builder.Services.AddDataProtection()
        .PersistKeysToAzureBlobStorage(dpConnectionString, containerName, blobName);

    var keyVaultKeyUri = dpSection["KeyVaultKeyUri"];
    if (!string.IsNullOrEmpty(keyVaultKeyUri))
    {
        dpBuilder.ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyUri), new DefaultAzureCredential());
    }
}

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
        .AddPostgreSqlStorageFactory())
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

var app = builder.Build();

app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();

