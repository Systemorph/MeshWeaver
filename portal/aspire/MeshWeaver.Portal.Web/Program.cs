using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();
builder.AddKeyedRedisClient("orleans-redis");

// Add services to the container.
builder.ConfigureWebPortalServices();
builder.UseOrleansMeshClient()
            .ConfigureWebPortalMesh()
            .ConfigureArticles()
    ;


var app = builder.Build();
app.StartPortalApplication();
