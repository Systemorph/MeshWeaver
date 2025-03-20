using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableClient("orleans-clustering");

// Create MeshAddress instance
var address = new MeshAddress();
builder.ConfigurePostgreSqlContext();

// Configure Orleans with Azure Table Storage
builder.UseOrleansMeshServer(address)
    .ConfigurePortalMesh()
    .AddEfCoreSerilog()
    ;

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
