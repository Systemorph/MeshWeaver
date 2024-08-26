using Autofac.Core;
using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans;
using MeshWeaver.Orleans.Contract;
using MeshWeaver.Overview;
using Orleans.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient(StorageProviders.OrleansRedis);
builder.Services.AddSingleton<IMeshCatalog, MeshCatalog>();
var address = new OrleansAddress();

builder.Host.UseMeshWeaver(address,
    config => config
        .ConfigureOrleansMesh(address, mesh => mesh.InstallAssemblies(typeof(MeshWeaverOverviewAttribute).Assembly.Location))
);


builder.UseOrleans(orleansBuilder =>
{
    if (builder.Environment.IsDevelopment())
    {
        orleansBuilder.ConfigureEndpoints(Random.Shared.Next(10_000, 50_000), Random.Shared.Next(10_000, 50_000));
    }
    orleansBuilder.Services.AddSerializer(serializerBuilder =>
    {
        serializerBuilder.AddJsonSerializer(
            type => true,
            type => true,
            ob =>
                ob.PostConfigure<IMessageHub>(
                    (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                )
        );
    });
});
var app = builder.Build();

app.Run();
