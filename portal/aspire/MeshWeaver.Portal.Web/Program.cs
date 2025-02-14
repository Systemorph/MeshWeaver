﻿using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Portal.ServiceDefaults;
using MeshWeaver.Portal.Shared.Mesh;
using MeshWeaver.Portal.Shared.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAspireServiceDefaults();
builder.AddKeyedRedisClient(StorageProviders.AddressRegistry);

// Add services to the container.
builder.ConfigurePortalApplication();
builder.UseOrleansMeshClient(new UiAddress())
            .ConfigureWebPortalMesh()
            .ConfigurePortalMesh()
    ;


var app = builder.Build();
app.StartPortalApplication();
