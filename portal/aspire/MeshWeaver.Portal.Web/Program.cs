using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();
builder.AddKeyedRedisClient(StorageProviders.Redis);

// Add services to the container.
var blazorAddress = new UiAddress();
builder.ConfigurePortalApplication();
builder.UseMeshWeaver(blazorAddress,
        configuration: config => config
            .UseOrleansMeshClient()
            .ConfigureWebPortalMesh()
            .ConfigurePortalMesh()
    );


var app = builder.Build();
app.StartPortalApplication();
