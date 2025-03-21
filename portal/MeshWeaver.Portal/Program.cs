﻿using MeshWeaver.Articles;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Messaging;
using MeshWeaver.Portal;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureWebPortalServices();
builder.AddServiceDefaults();


builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseMonolithMesh()
        .ConfigureServices(services => services.AddArticles())
);

var app = builder.Build();

app.StartPortalApplication();
