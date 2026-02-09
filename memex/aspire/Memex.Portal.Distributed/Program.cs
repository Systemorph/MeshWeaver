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
builder.AddKeyedAzureBlobServiceClient("storage");

// Read Cosmos connection string from Aspire configuration
// The name "memexdb" matches the database resource name in the AppHost:
//   cosmos.AddCosmosDatabase("memexdb") → .WithReference(cosmosDb)
var cosmosConnectionString = builder.Configuration.GetConnectionString("memexdb");

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
        .Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1);
        })
    )
    .ConfigureServices(services => services
        .AddCosmosStorageFactory(opts =>
        {
            opts.ConnectionString = cosmosConnectionString;
            opts.DatabaseName = "memexdb";
        })
        .AddCosmosSeeding(opts =>
        {
            opts.Enabled = builder.Environment.IsDevelopment();
            opts.SeedDataPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "../../../../samples/Graph/Data"));
        }))
    .ConfigureMemexMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureMemexPortal();

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartMemexApplication<Memex.Portal.Shared.App>();
