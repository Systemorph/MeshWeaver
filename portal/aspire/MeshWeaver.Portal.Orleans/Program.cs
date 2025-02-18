using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;

var builder = WebApplication.CreateBuilder(args);
builder.AddAspireServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient("orleans-redis");
builder.AddKeyedRedisClient(StorageProviders.AddressRegistry);
var address = new MeshAddress();



builder.
    UseOrleansMeshServer(address)
                .ConfigurePortalMesh()

    ;

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
