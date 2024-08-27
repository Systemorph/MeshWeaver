using Azure.Data.Tables;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using MeshWeaver.Overview;
using Microsoft.Azure.Cosmos;
using Orleans.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var address = new OrleansAddress();

builder.Host.UseMeshWeaver(address,
    config => config
        .AddOrleansMesh(address, mesh => mesh.InstallAssemblies(typeof(MeshWeaverOverviewAttribute).Assembly.Location))
);


builder.AddAzureCosmosClient(StorageProviders.Clustering);
builder.AddAzureCosmosClient(StorageProviders.Routing);
builder.AddAzureCosmosClient(StorageProviders.MeshCatalog);

builder.UseOrleans(siloBuilder =>
{
    if (builder.Environment.IsDevelopment())
    {
        siloBuilder.ConfigureEndpoints(Random.Shared.Next(10_000, 50_000), Random.Shared.Next(10_000, 50_000));
    }
    siloBuilder.UseCosmosClustering(
        configureOptions: static options =>
        {
            options.IsResourceCreationEnabled = true;
            options.DatabaseName = StorageProviders.Clustering;
            options.ContainerName = "OrleansCluster";
            options.ContainerThroughputProperties = ThroughputProperties.CreateAutoscaleThroughput(1000);
        });
    siloBuilder.Services.AddSerializer(serializerBuilder =>
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
