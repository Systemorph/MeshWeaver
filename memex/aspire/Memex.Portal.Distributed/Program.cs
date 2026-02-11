using Memex.Portal.ServiceDefaults;
using Memex.Portal.Shared;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Messaging;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Register Aspire-injected clients
builder.AddKeyedAzureTableServiceClient("orleans-clustering");
builder.AddKeyedAzureBlobServiceClient("storage");
builder.AddKeyedAzureBlobServiceClient("orleans-grain-state");

// Register Aspire-injected Cosmos database and containers as keyed services.
// The connection name "memexdb" matches the AppHost: cosmos.AddCosmosDatabase("memexdb")
builder.AddAzureCosmosDatabase("memexdb",
    configureClientOptions: cosmosOptions =>
    {
        cosmosOptions.UseSystemTextJsonSerializerWithOptions =
            StorageImporter.CreateFullImportOptions();
    })
    .AddKeyedContainer("nodes")
    .AddKeyedContainer("partitions");

// Add web portal services
builder.ConfigureMemexServices();

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
        .AddCosmosStorageFactory(opts =>
        {
            opts.DatabaseName = "memexdb";
        }))
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();
