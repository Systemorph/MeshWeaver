using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.ShortGuid;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("orleans-clustering");

builder.ConfigurePostgreSqlContext("meshweaverdb");

// Configure Orleans with Azure Table Storage
var serviceId = Guid.NewGuid().AsString();
builder.UseOrleansMeshServer(new MeshAddress(serviceId))
    .ConfigurePortalMesh()
    .AddEfCoreSerilog("Silo", serviceId)
    .AddEfCoreMessageLog("Silo", serviceId)
    ;

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
