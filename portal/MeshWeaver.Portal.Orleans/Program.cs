using MeshWeaver.Mesh.Contract;
using MeshWeaver.Orleans.Client;
using MeshWeaver.Orleans.Server;
using MeshWeaver.Portal.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient(StorageProviders.Redis);
var address = new OrleansAddress();

builder.AddOrleansMeshServer(address);

var app = builder.Build();

app.Run();
