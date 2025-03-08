using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;

var builder = WebApplication.CreateBuilder(args);
builder.AddAspireServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedAzureTableClient(StorageProviders.AddressRegistry);
builder.AddKeyedAzureTableClient("orleans-clustering");

// Create MeshAddress instance
var address = new MeshAddress();

// Configure Orleans with Azure Table Storage
builder.UseOrleansMeshServer(address)
    .ConfigurePortalMesh()
    .AddPostgresSerilog();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
