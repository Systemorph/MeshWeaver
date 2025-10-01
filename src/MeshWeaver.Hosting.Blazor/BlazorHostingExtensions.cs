using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.ContentCollections;
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
    public static MeshBuilder AddBlazor(this MeshBuilder builder, Func<LayoutClientConfiguration, LayoutClientConfiguration>? clientConfig = null) =>
        builder
            .ConfigureServices(services => services
                .AddFluentUIComponents()
                .AddScoped<PortalApplication>()
                .Configure<RouteOptions>(options =>
                    options.ConstraintMap.Add("addresstype", typeof(AddressTypeRouteConstraint)))
            )
            .ConfigureHub(hub => hub.AddBlazor(clientConfig));

    public static void MapMeshWeaver(this WebApplication app)
    {
        app.MapStaticContent(app.Services.GetRequiredService<IContentService>());
        app.UseMiddleware<UserContextMiddleware>();

        // Thumbnail preview stub (returns 501 until implemented)
        app.MapGet("/layout-preview/{area}", (string area) =>
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        });

        //app.MapRazorComponents<ApplicationPage>();
    }
    private static void MapStaticContent(this IEndpointRouteBuilder app, IContentService contentService)
        => app.MapGet("/static/{collection}/{**path}", async (HttpContext context, string collection, string path) =>
        {
            var stream = await contentService.GetContentAsync(collection, path);

            if (stream is null)
            {
                return Results.NotFound("File not found");
            }

            // Determine content type based on file extension
            //var contentType = "application/octet-stream";
            var contentType = GetContentType(path);

            // Configure caching headers

            if (stream.Length < 10_000_000) // Only compute hash for files smaller than 10MB
            {
                var cacheDuration = TimeSpan.FromDays(30);
                context.Response.Headers.CacheControl = $"public, max-age={cacheDuration.TotalSeconds}, immutable";
                context.Response.Headers.Expires = DateTime.UtcNow.AddDays(30).ToString("R");

                //Add ETag for cache
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
                context.Response.Headers.ETag = $"\"{hash}\"";
                return Results.File(ms.ToArray(), contentType, Path.GetFileName(path));
            }

            // Return the stream directly without loading it all into memory
            return Results.Stream(
                    stream,
                    contentType,
                    Path.GetFileName(path),
                    enableRangeProcessing: true);
        });

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".html" => "text/html",
            ".htm" => "text/html",
            ".json" => "application/json",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".eot" => "application/vnd.ms-fontobject",
            ".otf" => "font/otf",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }
}
