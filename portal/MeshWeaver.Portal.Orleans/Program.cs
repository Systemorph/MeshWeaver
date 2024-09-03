using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Hosting.Orleans.Server;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Overview;
using MeshWeaver.Portal.ServiceDefaults;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient(StorageProviders.Redis);
var address = new OrleansAddress();

builder.
    UseMeshWeaver(address, conf => 
        conf
            .ConfigureMesh(mesh => mesh.InstallAssemblies(typeof(MeshWeaverOverviewAttribute).Assembly.Location))
            .AddOrleansMeshServer());

var app = builder.Build();

app.Run();
