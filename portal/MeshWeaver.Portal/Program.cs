using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.Shared;
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigurePortalApplication();


builder.UseMeshWeaver(
    new UiAddress(),
    config => config
        .ConfigurePortalMesh()
        .UseMonolithMesh()
);

var app = builder.Build();

app.StartPortalApplication();
