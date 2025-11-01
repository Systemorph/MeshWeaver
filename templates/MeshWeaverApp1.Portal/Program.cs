using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;
using MeshWeaverApp1.Todo;
using MeshWeaverApp1.Portal;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureWebPortalServices();

builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseMonolithMesh()
        .ConfigureHub(hub => hub.AddContentCollections())
);

var app = builder.Build();

app.StartPortalApplication();
