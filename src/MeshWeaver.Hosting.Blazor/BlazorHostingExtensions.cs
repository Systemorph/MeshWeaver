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
                .AddScoped<INavigationContextService, NavigationContextService>()
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
    /// Maps static content endpoint supporting two path patterns:
    /// 1. /static/{collection}/{filePath} - when first segment is a known collection (e.g., content, attachments)
    /// 2. /static/{address}/{collection}/{filePath} - when first segment is an address
    ///
    /// The endpoint first checks if the first segment matches a registered collection name.
    /// If yes, serves from the mesh hub's content service directly.
    /// If no, uses address-based resolution via IMeshCatalog.ResolvePath.
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
                var pathParts = path.Split('/');
                if (pathParts.Length < 2)
                {
                    return Results.NotFound("Invalid path format. Expected: /static/{collection}/{filePath} or /static/{address}/{collection}/{filePath}");
                }

                var firstSegment = pathParts[0];

                // Check if first segment is a known collection name
                var contentService = mainHub.ServiceProvider.GetService<IContentService>();
                var knownCollection = contentService?.GetCollectionConfig(firstSegment);

                if (knownCollection != null)
                {
                    // Pattern 1: /static/{collection}/{filePath}
                    var collectionName = firstSegment;
                    var filePath = string.Join("/", pathParts.Skip(1));

                    return await ServeFromCollection(context, mainHub, collectionName, filePath, collectionCache);
                }

                // Pattern 2: /static/{address}/{collection}/{filePath}
                // Resolve address from path using score-based matching
                var meshCatalog = mainHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                var resolution = await meshCatalog.ResolvePathAsync(path);

                if (resolution == null)
                {
                    return Results.NotFound("No matching address found for path");
                }

                // Parse remainder: first segment is collection, rest is file path
                if (string.IsNullOrEmpty(resolution.Remainder))
                {
                    return Results.NotFound("Collection and file path are required");
                }

                var remainderParts = resolution.Remainder.Split('/');
                if (remainderParts.Length < 2)
                {
                    return Results.NotFound("Invalid path format. Expected: /static/{address}/{collection}/{filePath}");
                }

                var addressCollectionName = remainderParts[0];
                var addressFilePath = string.Join("/", remainderParts.Skip(1));

                if (string.IsNullOrEmpty(addressFilePath))
                {
                    return Results.NotFound("File path is required");
                }

                var targetAddress = (Address)resolution.Prefix;
                var qualifiedCollectionName = $"{resolution.Prefix}/{addressCollectionName}";

                // Get or fetch collection configuration from target hub
                if (!collectionCache.TryGetValue(qualifiedCollectionName, out var collectionConfig))
                {
                    var collectionResponse = await mainHub.AwaitResponse(
                        new GetDataRequest(new ContentCollectionReference([addressCollectionName])),
                        o => o.WithTarget(targetAddress),
                        context.RequestAborted);

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
                    var sourceConfig = configs?.FirstOrDefault(c => c.Name == addressCollectionName);

                    if (sourceConfig == null)
                    {
                        return Results.NotFound($"Content collection '{addressCollectionName}' not found at {resolution.Prefix}");
                    }

                    collectionConfig = sourceConfig with
                    {
                        Name = qualifiedCollectionName,
                        Address = targetAddress
                    };

                    collectionCache.TryAdd(qualifiedCollectionName, collectionConfig);
                }

                // Get content service from portal
                var portal = mainHub.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
                var portalContentService = portal.ServiceProvider.GetService<IContentService>();
                if (portalContentService is null)
                {
                    return Results.NotFound("Content service not configured");
                }

                portalContentService.AddConfiguration(collectionConfig);

                var contentCollection = await portalContentService.GetCollectionAsync(qualifiedCollectionName, context.RequestAborted);
                if (contentCollection == null)
                {
                    return Results.NotFound($"Content collection '{addressCollectionName}' not found");
                }

                return await ServeFile(context, contentCollection, addressFilePath);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error retrieving static content: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Serves a file from a known collection (pattern: /static/{collection}/{filePath}).
    /// </summary>
    private static async Task<IResult> ServeFromCollection(
        HttpContext context,
        IMessageHub hub,
        string collectionName,
        string filePath,
        System.Collections.Concurrent.ConcurrentDictionary<string, ContentCollectionConfig> _)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return Results.NotFound("File path is required");
        }

        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService is null)
        {
            return Results.NotFound("Content service not configured");
        }

        var contentCollection = await contentService.GetCollectionAsync(collectionName, context.RequestAborted);
        if (contentCollection == null)
        {
            return Results.NotFound($"Content collection '{collectionName}' not found");
        }

        return await ServeFile(context, contentCollection, filePath);
    }

    /// <summary>
    /// Serves a file from a content collection with proper caching headers.
    /// </summary>
    private static async Task<IResult> ServeFile(HttpContext context, ContentCollection contentCollection, string filePath)
    {
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

}
