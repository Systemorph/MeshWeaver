﻿using MeshWeaver.Application;
using MeshWeaver.Blazor;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh.Contract;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

public  static class BlazorHostingExtensions
{
    public static MeshWeaverHostBuilder AddBlazor(this MeshWeaverHostBuilder builder, Func<LayoutClientConfiguration, LayoutClientConfiguration> clientConfig = null)
    {
        builder.Host.Services.AddRazorPages();
        builder.Host.Services.AddServerSideBlazor();
        builder.Host.Services.AddFluentUIComponents();

        return builder.ConfigureHub(hub => hub.AddBlazor(clientConfig));

    }

    public static void MapStaticContent(this IEndpointRouteBuilder app, IMeshCatalog meshCatalog)
        => app.MapGet("/static/{application}/{environment}/{*fileName}", async (string application, string environment, string fileName) =>
    {
        var address = new ApplicationAddress(application, environment);
        var storageInfo = await meshCatalog.GetNodeAsync(address.ToString());
        var filePath = Path.Combine(storageInfo.BasePath, storageInfo.ContentPath, fileName);

        if (!File.Exists(filePath))
        {
            return Results.NotFound("File not found");
        }

        var fileContent = await File.ReadAllBytesAsync(filePath);
        var contentType = "application/octet-stream"; // Default content type, you can adjust based on file type

        return Results.File(fileContent, contentType, fileName);
    });
}
