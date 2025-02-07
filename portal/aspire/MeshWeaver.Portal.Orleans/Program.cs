using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Services;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddAspireServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient(StorageProviders.Redis);
var address = new OrleansAddress();


builder.AddAspireServiceDefaults();

builder.
    UseMeshWeaver(address, conf =>
        conf
            .UseOrleansMeshServer()
            .ConfigurePortalMesh()
            )
    ;

var app = builder.Build();

app.Run();
