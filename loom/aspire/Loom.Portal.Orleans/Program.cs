using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using Loom.Portal.ServiceDefaults;
using Loom.Portal.Shared;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("orleans-clustering");

// Add web portal services
builder.ConfigureLoomServices();

// Configure Orleans with Azure Table Storage (co-hosted silo + web)
var address = AddressExtensions.CreateMeshAddress();
builder.UseOrleansMeshServer(address, silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = LoomOrleansConstants.ClusterId;
            opts.ServiceId = LoomOrleansConstants.ServiceId;
        })
        .Configure<ConnectionOptions>(options =>
        {
            options.OpenConnectionTimeout = TimeSpan.FromMinutes(1);
        })
    )
    .ConfigureLoomMesh(builder.Configuration, builder.Environment.IsDevelopment())
    .ConfigureLoomPortal();

var app = builder.Build();
app.MapDefaultEndpoints();
app.StartLoomApplication<Loom.Portal.Shared.App>();
