using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh;
using MeshWeaver.Portal;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigurePortalApplication();
builder.AddServiceDefaults();


builder.UseMeshWeaver(
    new UiAddress(),
    config => config
        .ConfigureWebPortalMesh()
        .ConfigurePortalMesh()
        .UseMonolithMesh()
);

var app = builder.Build();

app.StartPortalApplication();
