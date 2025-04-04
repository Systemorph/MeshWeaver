﻿using MeshWeaver.Articles;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

public static class BlazorHostingExtensions
{
    public static MeshBuilder AddBlazor(this MeshBuilder builder, Func<LayoutClientConfiguration, LayoutClientConfiguration> clientConfig = null) =>
        builder
            .ConfigureServices(services => services
                .AddFluentUIComponents()
                .AddScoped<PortalApplication>()
            )
            .ConfigureHub(hub => hub.AddBlazor(clientConfig));

    public static void MapMeshWeaver(this IEndpointRouteBuilder app)
    {
        app.MapStaticContent(app.ServiceProvider.GetRequiredService<IArticleService>());
        //app.MapRazorComponents<ApplicationPage>();
    }
    private static void MapStaticContent(this IEndpointRouteBuilder app, IArticleService articleService)
        => app.MapGet("/static/{collection}/{**path}", async (string collection, string path) =>
        {
            var stream = await articleService.GetContentAsync(collection, path);

            if (stream is null)
            {
                return Results.NotFound("File not found");
            }

            var contentType = "application/octet-stream"; // Default content type, you can adjust based on file type

            return Results.File(stream, contentType, path);
        });
}
