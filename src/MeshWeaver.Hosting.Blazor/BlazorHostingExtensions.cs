using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.ContentCollections;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
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
        app.MapStaticContent(app.Services.GetRequiredService<IMessageHub>());
        app.MapHubStaticContent(app.Services.GetRequiredService<IMessageHub>());
        app.UseMiddleware<UserContextMiddleware>();

        // Thumbnail preview stub (returns 501 until implemented)
        app.MapGet("/layout-preview/{area}", (string area) =>
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        });

        //app.MapRazorComponents<ApplicationPage>();
    }
    private static void MapStaticContent(this IEndpointRouteBuilder app, IMessageHub hub)
        => app.MapGet("/static/{collection}/{**path}", async (HttpContext context, string collection, string path) =>
        {
            var contentService = hub.ServiceProvider.GetRequiredService<PortalApplication>().Hub.ServiceProvider
                .GetRequiredService<IContentService>();
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
            ".pdf" => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".zip" => "application/zip",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            _ => "application/octet-stream"
        };
    }

    private static bool IsTextContentType(string contentType)
    {
        var textTypes = new[]
        {
            "text/css",
            "application/javascript",
            "text/html",
            "application/json",
            "text/plain",
            "text/markdown",
            "image/svg+xml"
        };

        return textTypes.Contains(contentType) || contentType.StartsWith("text/");
    }

    private static void MapHubStaticContent(this IEndpointRouteBuilder app, IMessageHub mainHub)
        => app.MapGet("/{addressType:addresstype}/{addressId}/static/{collection}/{**path}",
            async (HttpContext context, string addressType, string addressId, string collection, string path) =>
        {
            try
            {
                var portal = mainHub.ServiceProvider.GetRequiredService<PortalApplication>().Hub;

                // Get content service directly from the target hub
                var contentService = portal.ServiceProvider.GetService<IContentService>();
                if (contentService is null)
                {
                    return Results.NotFound("Content service not configured");
                }

                contentService.AddConfiguration(new ContentCollectionConfig()
                {
                    Name = collection,
                    SourceType = HubContentCollectionFactory.SourceType,
                    Address = portal.TypeRegistry.MapAddress(addressType, addressId)
                });

                // Get or initialize the collection by name
                var contentCollection = await contentService.GetCollectionAsync(collection, context.RequestAborted);

                if (contentCollection == null)
                {
                    return Results.NotFound($"Content collection '{collection}' not found");
                }

                // Get stream directly from the collection
                var stream = await contentCollection.GetContentAsync(path, context.RequestAborted);
                if (stream == null)
                {
                    return Results.NotFound("File not found");
                }

                var contentType = GetContentType(path);
                var fileName = Path.GetFileName(path);

                // Check if download is requested via query parameter
                var isDownload = context.Request.Query.ContainsKey("download");
                if (isDownload)
                {
                    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                }

                // Configure caching headers for small files
                if (stream.Length < 10_000_000) // Only compute hash for files smaller than 10MB
                {
                    var cacheDuration = TimeSpan.FromDays(30);
                    context.Response.Headers.CacheControl = $"public, max-age={cacheDuration.TotalSeconds}, immutable";
                    context.Response.Headers.Expires = DateTime.UtcNow.AddDays(30).ToString("R");

                    // Add ETag for cache
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, context.RequestAborted);
                    ms.Position = 0;
                    var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
                    context.Response.Headers.ETag = $"\"{hash}\"";
                    return Results.File(ms.ToArray(), contentType, fileName);
                }

                // Return the stream directly without loading it all into memory
                return Results.Stream(
                    stream,
                    contentType,
                    fileName,
                    enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error retrieving static content: {ex.Message}");
            }
        });

}
