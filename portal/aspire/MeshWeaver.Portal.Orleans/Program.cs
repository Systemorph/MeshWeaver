using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
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

app.Run();
