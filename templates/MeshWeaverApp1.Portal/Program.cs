using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using MeshWeaverApp1.Portal;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureWebPortalServices();

builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseMonolithMesh()
        .ConfigureServices(services => services.AddContentCollections())
);

var app = builder.Build();

app.StartPortalApplication();
