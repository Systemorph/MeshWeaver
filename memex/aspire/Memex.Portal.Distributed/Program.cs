using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Register Aspire-injected clients
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddAzureCosmosClient("memexcosmos");
builder.AddKeyedAzureBlobServiceClient("storage");

// Add web portal services
builder.ConfigureMemexServices();

// Bridge Aspire Cosmos connection string to Graph:Storage config
var cosmosConnectionString = builder.Configuration.GetConnectionString("memexcosmos");
if (!string.IsNullOrEmpty(cosmosConnectionString))
{
    builder.Configuration["Graph:Storage:ConnectionString"] = cosmosConnectionString;
}

// Configure Orleans with Azure Table Storage (co-hosted silo + web)
var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = MemexOrleansConstants.ClusterId;
            opts.ServiceId = MemexOrleansConstants.ServiceId;
        })
        .Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1);
        })
    )
    .ConfigureServices(services => services.AddCosmosStorageFactory())
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();
