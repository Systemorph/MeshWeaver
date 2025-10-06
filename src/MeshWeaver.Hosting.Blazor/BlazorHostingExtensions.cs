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
        => app.MapGet("/{addressType:addresstype}/{addressId}/static/{**path}",
            async (HttpContext context, string addressType, string addressId, string path) =>
        {
            // Get or create the hub for the specified address
            var address = MeshExtensions.MapAddress(addressType, addressId);

            // Send request to hub to get static content
            var request = new GetStaticContentRequest(path);
            try
            {
                var application = mainHub.ServiceProvider.GetRequiredService<PortalApplication>();
                var cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                var response = await application.Hub.AwaitResponse(request, o => o.WithTarget(address), cancellationToken.Token);

                if (!response.Message.IsFound)
                {
                    return Results.NotFound("File not found");
                }

                var contentType = response.Message.ContentType ?? GetContentType(path);
                var fileName = response.Message.FileName ?? Path.GetFileName(path);

                // Handle inline content
                if (response.Message.SourceType == GetStaticContentResponse.InlineSourceType && response.Message.InlineContent != null)
                {
                    var cacheDuration = TimeSpan.FromDays(30);
                    context.Response.Headers.CacheControl = $"public, max-age={cacheDuration.TotalSeconds}, immutable";
                    context.Response.Headers.Expires = DateTime.UtcNow.AddDays(30).ToString("R");

                    // Determine if content is base64-encoded binary or plain text
                    byte[] bytes;
                    if (IsTextContentType(contentType))
                    {
                        // Text content - use as-is
                        bytes = System.Text.Encoding.UTF8.GetBytes(response.Message.InlineContent);
                    }
                    else
                    {
                        // Binary content - decode from base64
                        bytes = Convert.FromBase64String(response.Message.InlineContent);
                    }

                    var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes));
                    context.Response.Headers.ETag = $"\"{hash}\"";

                    return Results.File(bytes, contentType, fileName);
                }

                // Handle provider-based content
                if (response.Message.ProviderReference != null)
                {
                    var streamProvider = GetStreamProvider(context.RequestServices, response.Message.ProviderName);
                    if (streamProvider == null)
                    {
                        return Results.Problem($"Stream provider not found: {response.Message.ProviderName}");
                    }

                    var stream = await streamProvider.GetStreamAsync(response.Message.ProviderReference, context.RequestAborted);
                    if (stream == null)
                    {
                        return Results.NotFound("File not found");
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

                return Results.NotFound("File not found");

            }
            catch (TaskCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error retrieving static content: {ex.Message}");
            }
        });

    private static IStreamProvider? GetStreamProvider(IServiceProvider services, string? providerName)
    {
        if (string.IsNullOrEmpty(providerName))
        {
            return null;
        }

        return services.GetKeyedService<IStreamProvider>(providerName);
    }
}
