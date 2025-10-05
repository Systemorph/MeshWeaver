using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.ShortGuid;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("orleans-clustering");

builder.ConfigurePostgreSqlContext("meshweaverdb");

// Configure Orleans with Azure Table Storage
var serviceId = OrleansConstants.ServiceId;
builder.UseOrleansMeshServer(new MeshAddress(serviceId), silo =>
        silo.Configure<ClusterOptions>(opts =>
        {
            opts.ClusterId = OrleansConstants.ClusterId;
            opts.ServiceId = OrleansConstants.ServiceId;
        })
    )
    .ConfigurePortalMesh()
    .AddEfCoreSerilog("Silo", serviceId)
    .AddEfCoreMessageLog("Silo", serviceId)
    ;

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
