using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using Loom.Portal.ServiceDefaults;
using Loom.Portal.Shared;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Register Aspire-injected clients
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddAzureCosmosClient("loomcosmos");
builder.AddKeyedAzureBlobServiceClient("storage");

// Add web portal services
builder.ConfigureLoomServices();

// Bridge Aspire Cosmos connection string to Graph:Storage config
var cosmosConnectionString = builder.Configuration.GetConnectionString("loomcosmos");
if (!string.IsNullOrEmpty(cosmosConnectionString))
{
    builder.Configuration["Graph:Storage:ConnectionString"] = cosmosConnectionString;
}

// Configure Orleans with Azure Table Storage (co-hosted silo + web)
var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = LoomOrleansConstants.ClusterId;
            opts.ServiceId = LoomOrleansConstants.ServiceId;
        })
        .Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1);
        })
    )
    .ConfigureServices(services => services.AddCosmosStorageFactory())
    .ConfigureLoomMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureLoomPortal();

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartLoomApplication<Loom.Portal.Shared.App>();
