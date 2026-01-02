using System.Text.Json;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
                .AddContentService()
                .AddFluentUIComponents()
                .AddScoped<PortalApplication>()
            )
            .ConfigureHub(hub => hub.AddBlazor(clientConfig));

    public static void MapMeshWeaver(this WebApplication app)
    {
        app.MapStaticContent(app.Services);
        app.UseMiddleware<UserContextMiddleware>();

        // Thumbnail preview stub (returns 501 until implemented)
        app.MapGet("/layout-preview/{area}", (string area) => Results.StatusCode(StatusCodes.Status501NotImplemented));

        //app.MapRazorComponents<ApplicationPage>();
    }

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

    /// <summary>
    /// Maps static content endpoint using score-based path resolution.
    /// Routes like: /static/{**path}
    /// Example: /static/app/Northwind/Northwind/thumbnails/logo.png
    /// The path is resolved via IMeshCatalog.ResolvePath to get the prefix and remainder.
    /// First segment of remainder is collection name, rest is file path.
    /// Uses GetDataRequest with ContentCollectionReference to obtain collection configuration from the target hub.
    /// </summary>
    private static void MapStaticContent(this IEndpointRouteBuilder app, IServiceProvider services)
    {
        // Collection configuration cache: key = "prefix/collection"
        var collectionCache = new System.Collections.Concurrent.ConcurrentDictionary<string, ContentCollectionConfig>();
        // Lazy resolution of IMessageHub to avoid circular dependency during startup
        IMessageHub? mainHub = null;

        app.MapGet("/static/{**path}",
            async (HttpContext context, string path) =>
        {
            // Resolve hub on first request
            mainHub ??= services.GetRequiredService<IMessageHub>();

            if (string.IsNullOrEmpty(path))
            {
                return Results.NotFound("Path is required");
            }

            try
            {
                // Resolve address from path using score-based matching
                var meshCatalog = mainHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                var resolution = await meshCatalog.ResolvePathAsync(path);

                if (resolution == null)
                {
                    return Results.NotFound("No matching address found for path");
                }

                // Parse remainder: first segment is collection, rest is file path
                // Example: remainder = "Northwind/thumbnails/logo.png" → collection="Northwind", filePath="thumbnails/logo.png"
                if (string.IsNullOrEmpty(resolution.Remainder))
                {
                    return Results.NotFound("Collection and file path are required");
                }

                var remainderParts = resolution.Remainder.Split('/');
                if (remainderParts.Length < 2)
                {
                    return Results.NotFound("Invalid path format. Expected: /static/{address}/{collection}/{filePath}");
                }

                var collectionName = remainderParts[0];
                var filePath = string.Join("/", remainderParts.Skip(1));

                if (string.IsNullOrEmpty(filePath))
                {
                    return Results.NotFound("File path is required");
                }

                var targetAddress = (Address)resolution.Prefix;
                // Use qualified collection name to avoid collisions between different hubs
                // e.g., "ACME/logos" vs "Systemorph/logos"
                var qualifiedCollectionName = $"{resolution.Prefix}/{collectionName}";

                // Get or fetch collection configuration
                if (!collectionCache.TryGetValue(qualifiedCollectionName, out var collectionConfig))
                {
                    // Request collection configuration from the target hub using GetDataRequest with ContentCollectionReference
                    var collectionResponse = await mainHub.AwaitResponse(
                        new GetDataRequest(new ContentCollectionReference([collectionName])),
                        o => o.WithTarget(targetAddress),
                        context.RequestAborted);

                    // Handle both direct type and JSON serialized response
                    IReadOnlyCollection<ContentCollectionConfig>? configs;
                    if (collectionResponse?.Message?.Data is JsonElement jsonElement)
                    {
                        configs = jsonElement.EnumerateArray()
                            .Select(e => new ContentCollectionConfig
                            {
                                Name = e.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                                SourceType = e.TryGetProperty("sourceType", out var typeProp) ? typeProp.GetString() ?? "" : "",
                                BasePath = e.TryGetProperty("basePath", out var pathProp) ? pathProp.GetString() : null
                            })
                            .ToArray();
                    }
                    else
                    {
                        configs = collectionResponse?.Message?.Data as IReadOnlyCollection<ContentCollectionConfig>;
                    }
                    var sourceConfig = configs?.FirstOrDefault(c => c.Name == collectionName);

                    if (sourceConfig == null)
                    {
                        return Results.NotFound($"Content collection '{collectionName}' not found at {resolution.Prefix}");
                    }

                    // Create config with qualified name to avoid collisions
                    collectionConfig = sourceConfig with
                    {
                        Name = qualifiedCollectionName,
                        Address = targetAddress
                    };

                    // Cache the collection configuration
                    collectionCache.TryAdd(qualifiedCollectionName, collectionConfig);
                }

                // Get content service from portal
                var portal = mainHub.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
                var contentService = portal.ServiceProvider.GetService<IContentService>();
                if (contentService is null)
                {
                    return Results.NotFound("Content service not configured");
                }

                // Add the collection configuration with qualified name
                contentService.AddConfiguration(collectionConfig);

                // Get the collection using qualified name
                var contentCollection = await contentService.GetCollectionAsync(qualifiedCollectionName, context.RequestAborted);
                if (contentCollection == null)
                {
                    return Results.NotFound($"Content collection '{collectionName}' not found");
                }

                var stream = await contentCollection.GetContentAsync(filePath, context.RequestAborted);
                if (stream == null)
                {
                    return Results.NotFound("File not found");
                }

                var contentType = GetContentType(filePath);
                var fileName = Path.GetFileName(filePath);

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

}
