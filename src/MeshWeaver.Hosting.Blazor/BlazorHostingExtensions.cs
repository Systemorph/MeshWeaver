using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mcp;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Blazor.Services;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Extension methods that wire the Blazor portal into a <c>MeshBuilder</c> and map the
/// MeshWeaver HTTP endpoints (static content, layout previews) onto a <c>WebApplication</c>.
/// </summary>
public static class BlazorHostingExtensions
{
    /// <summary>
    /// Registers the Blazor portal services (content service, FluentUI components, circuit
    /// access handling, navigation and menu providers) and configures the hub's Blazor layout
    /// client.
    /// </summary>
    /// <param name="builder">The mesh builder to add the Blazor portal to.</param>
    /// <param name="clientConfig">Optional callback to customize the layout client configuration.</param>
    /// <returns>The same <paramref name="builder"/> instance for chaining.</returns>
    public static MeshBuilder AddBlazor(this MeshBuilder builder, Func<LayoutClientConfiguration, LayoutClientConfiguration>? clientConfig = null) =>
        builder
            .ConfigureServices(services => services
                .AddContentService()
                .AddFluentUIComponents()
                .AddSingleton<UserIdentityCache>()
                .AddScoped<ICircuitContextAccessor, CircuitContextAccessor>()
                .AddScoped<PortalApplication>()
                .AddScoped<PortalErrorSink>()
                .AddScoped<INavigationService, NavigationService>()
                .AddScoped<IMenuItemsProvider, MenuItemsProvider>()
                .AddScoped<CircuitAccessHandler>()
                .AddScoped<CircuitHandler>(sp => sp.GetRequiredService<CircuitAccessHandler>())
                .AddMeshMcp()
            )
            .ConfigureHub(hub => hub.AddBlazor(clientConfig));

    /// <summary>
    /// Maps the MeshWeaver HTTP endpoints onto the application: the static content endpoint
    /// and the layout preview stub.
    /// </summary>
    /// <param name="app">The web application to map the endpoints onto.</param>
    public static void MapMeshWeaver(this WebApplication app)
    {
        app.MapStaticContent(app.Services);

        // Thumbnail preview stub (returns 501 until implemented)
        app.MapGet("/layout-preview/{area}", (string area) => Results.StatusCode(StatusCodes.Status501NotImplemented));

        //app.MapRazorComponents<ApplicationPage>();
    }

    internal static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            // 🎬 Media. Without these a video served from a content collection falls to
            // application/octet-stream, and a browser will not play an octet-stream in a
            // <video> element — the course cover renders an empty player.
            ".mp4" => "video/mp4",
            ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".ogv" => "video/ogg",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".oga" => "audio/ogg",
            ".ogg" => "audio/ogg",
            ".vtt" => "text/vtt",
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

    /// <summary>
    /// The <c>fileDownloadName</c> to hand to <c>Results.File</c>/<c>Results.Stream</c>:
    /// the file name ONLY when a download was explicitly requested, otherwise <c>null</c>.
    ///
    /// <para>This is the whole inline-vs-attachment decision. Supplying a name always emits
    /// <c>Content-Disposition: attachment</c>, and a browser will not render an attachment inline —
    /// which is why every content-collection video played nowhere while the bytes were perfectly
    /// fine. Pure, so the rule is pinned without an HTTP round trip.</para>
    /// </summary>
    internal static string? DownloadNameFor(bool isDownloadRequested, string fileName) =>
        isDownloadRequested ? fileName : null;

    /// <summary>
    /// Shapes the HTTP result for a content-collection file: content type, inline-vs-attachment,
    /// and range processing, in ONE place.
    ///
    /// <para>This exists so the contract can be pinned at the level it actually broke — the
    /// RESPONSE. Testing <see cref="GetContentType"/> and <see cref="DownloadNameFor"/> in
    /// isolation would not have caught the original defect, because neither was wrong: the bug was
    /// that the file name was handed to <c>Results.File</c> unconditionally, which forces
    /// <c>Content-Disposition: attachment</c>. A test that asserts the emitted headers catches that
    /// reintroduction; a test of the helpers alone stays green while the video breaks.</para>
    /// </summary>
    internal static IResult FileResultFor(byte[] bytes, string filePath, bool isDownloadRequested) =>
        Results.File(
            bytes,
            GetContentType(filePath),
            DownloadNameFor(isDownloadRequested, Path.GetFileName(filePath)),
            enableRangeProcessing: true);

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
    ///
    /// <para>🚨 TWO EXPLICIT DECLARATIONS gate this route (issue #587), both defaulting to the
    /// closed value:</para>
    /// <list type="number">
    /// <item><see cref="ContentCollectionConfig.IsStatic"/> — is the collection MOUNTED here at
    ///   all? Not declared ⇒ 404, for every caller alike. Content must be mounted explicitly;
    ///   merely being registered on a hub never publishes it.</item>
    /// <item><see cref="ContentCollectionConfig.IsPublic"/> — is the mount world-readable? Not
    ///   declared ⇒ the file is attributed to its owning mesh node and the caller must hold
    ///   <see cref="MeshWeaver.Mesh.Security.Permission.Read"/> on it (<see cref="StaticContentGate"/>),
    ///   the same decision <c>AccessControlPipeline</c> applies to the <c>GetDataRequest</c> behind
    ///   an ordinary <c>/content/…</c> read.</item>
    /// </list>
    /// </summary>
    private static void MapStaticContent(this IEndpointRouteBuilder app, IServiceProvider services)
    {
        // Collection configuration cache: key = "prefix/collection"
        var collectionCache = new System.Collections.Concurrent.ConcurrentDictionary<string, ContentCollectionConfig>();
        // Lazy resolution of IMessageHub to avoid circular dependency during startup
        IMessageHub? mainHub = null;

        app.MapMethods("/static/{**path}", ["GET", "HEAD"],
            (HttpContext context, string path) =>
        {
            // Resolve hub on first request
            mainHub ??= services.GetRequiredService<IMessageHub>();

            if (string.IsNullOrEmpty(path))
                return Task.FromResult(Results.NotFound("Path is required"));

            // 🚨 Resolve the caller SYNCHRONOUSLY, before any scheduler hop. AccessContext is an
            // AsyncLocal and does not survive the Rx hops below, and concurrent /static requests
            // carry different users — so the identity is captured here per request and threaded
            // explicitly through the chain, never re-read from the ambient service downstream.
            var caller = ResolveStaticCaller(context, mainHub);

            // Compose the entire endpoint as IObservable<IResult>; the HTTP framework
            // boundary mandates Task<IResult>, so we ToTask once at the very end.
            // No await on hub round-trips — chain via SelectMany; deadlock surface
            // (await pathResolver.ResolvePath / hub.Observe) eliminated.
            return ResolveStatic(context, mainHub, path, caller, collectionCache)
                .Catch<IResult, Exception>(ex =>
                    Observable.Return(Results.Problem($"Error retrieving static content: {ex.Message}")))
                .FirstAsync()
                .ToTask(context.RequestAborted);
        });
    }

    /// <summary>
    /// The identity of a <c>/static</c> request. NEVER null — an unauthenticated caller resolves to
    /// the well-known Anonymous context, whose permissions are exactly the Anonymous grants.
    ///
    /// <para><c>UserContextMiddleware</c> runs for <c>/static</c> (it is deliberately NOT in its
    /// <c>ExcludedPrefixes</c> — #666) and stamps the fully-resolved identity on the mesh-wide
    /// <c>AccessService</c>, including the Bearer-token path that a claims principal alone cannot
    /// see. Prefer that. Fall back to resolving the claims principal directly for hosts that do not
    /// run the middleware, so the gate can never be silently defeated by a missing middleware.</para>
    /// </summary>
    private static AccessContext ResolveStaticCaller(HttpContext context, IMessageHub mainHub)
    {
        // The mesh registers ONE AccessService singleton and every hosted hub inherits it
        // (MessageHubConfiguration only adds its own when the parent scope has none), so this is
        // the very instance the middleware wrote to.
        var ambient = mainHub.ServiceProvider.GetService<AccessService>()?.Context;
        return ambient is not null && !string.IsNullOrEmpty(ambient.ObjectId)
            ? ambient
            : UserContextMiddleware.ResolveHttpCaller(context.User, mainHub.ServiceProvider);
    }

    private static IObservable<IResult> ResolveStatic(
        HttpContext context,
        IMessageHub mainHub,
        string path,
        AccessContext caller,
        System.Collections.Concurrent.ConcurrentDictionary<string, ContentCollectionConfig> collectionCache)
    {
        var pathParts = path.Split('/');
        if (pathParts.Length < 2)
            return Observable.Return(Results.NotFound(
                "Invalid path format. Expected: /static/{collection}/{filePath} or /static/{address}/{collection}/{filePath}"));

        var logger = mainHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(StaticContentGate));

        var firstSegment = pathParts[0];
        var decodedFirstSegment = DecodeCollectionName(firstSegment);
        var contentService = mainHub.ServiceProvider.GetService<IContentService>();
        var knownCollection = contentService?.GetCollectionConfig(decodedFirstSegment);

        if (knownCollection != null)
        {
            // Pattern 1: /static/{collection}/{filePath}
            var collectionName = decodedFirstSegment;

            // 🚨 MOUNT CHECK. /static serves ONLY collections that explicitly declare
            // IsStatic — anything else is not on this route at all. IsStatic existed but was
            // read NOWHERE, so every collection registered anywhere on the mesh hub was
            // servable by URL. Being unmounted is not an access decision, so it answers 404
            // ahead of the gate and discloses nothing about the caller's rights.
            if (!knownCollection.IsStatic)
                return Observable.Return(NotMounted(collectionName));

            var filePath = DecodeContentPath(string.Join("/", pathParts.Skip(1)));
            var serve = Observable.Defer(() =>
                (mainHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded)
                    .Run(_ => ServeFromCollection(context, mainHub, collectionName, filePath, knownCollection.IsPublic)));

            // 🚨 A collection declared IsPublic serves to anyone — that is the deliberate
            // opt-in (node-type icons must render on the login page). Everything else is
            // attributed to its owning node and gated on Read.
            if (knownCollection.IsPublic)
                return serve;

            return StaticContentGate
                .Authorize(mainHub, caller, StaticContentGate.RootStoreOwnerCandidate(filePath), logger)
                .SelectMany(verdict => verdict == StaticContentGate.Verdict.Allowed
                    ? serve
                    : Observable.Return(StaticContentGate.DenyResult(verdict)));
        }

        // Pattern 2: /static/{address}/{collection}/{filePath} — observable chain.
        var pathResolver = mainHub.ServiceProvider.GetRequiredService<IPathResolver>();
        return pathResolver.ResolvePath(path).SelectMany(resolution =>
        {
            if (resolution == null)
                return Observable.Return(Results.NotFound("No matching address found for path"));
            if (string.IsNullOrEmpty(resolution.Remainder))
                return Observable.Return(Results.NotFound("Collection and file path are required"));

            var remainderParts = resolution.Remainder.Split('/');
            if (remainderParts.Length < 2)
                return Observable.Return(Results.NotFound(
                    "Invalid path format. Expected: /static/{address}/{collection}/{filePath}"));

            var addressCollectionName = DecodeCollectionName(remainderParts[0]);
            var addressFilePath = DecodeContentPath(string.Join("/", remainderParts.Skip(1)));
            if (string.IsNullOrEmpty(addressFilePath))
                return Observable.Return(Results.NotFound("File path is required"));

            var targetAddress = (Address)resolution.Prefix;
            var qualifiedCollectionName = $"{resolution.Prefix}/{addressCollectionName}";

            // 🚨 STAMP THE CALLER on the post (#694). A hub post with no AccessContext is refused
            // by the never-null PostPipeline guard — invisible at one replica (local delivery) and
            // fatal at two, where whenever the target partition's hub lived on the OTHER silo the
            // cross-silo post died with "AccessContext must never be null" and the file 500'd (the
            // reason memex ran single-replica). The identity was resolved per-request at the
            // endpoint and threaded in as `caller` — never re-read from the ambient AccessService
            // here, which the Rx hops above have already left behind.

            // Cached collection config — short-circuit; otherwise compose hub.Observe.
            IObservable<ContentCollectionConfig?> configObs = collectionCache.TryGetValue(qualifiedCollectionName, out var cached)
                ? Observable.Return<ContentCollectionConfig?>(cached)
                : mainHub.Observe(
                        new GetDataRequest(new ContentCollectionReference([addressCollectionName])),
                        o => o.WithTarget(targetAddress).WithAccessContext(caller))
                    .Select<IMessageDelivery, ContentCollectionConfig?>(collectionResponse =>
                    {
                        IReadOnlyCollection<ContentCollectionConfig>? configs = collectionResponse?.Message switch
                        {
                            GetDataResponse { Data: JsonElement je } => je.EnumerateArray()
                                .Select(e => new ContentCollectionConfig
                                {
                                    Name = e.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                                    SourceType = e.TryGetProperty("sourceType", out var typeProp) ? typeProp.GetString() ?? "" : "",
                                    BasePath = e.TryGetProperty("basePath", out var pathProp) ? pathProp.GetString() : null,
                                    // 🚨 The serving classification MUST survive the wire. This hand-rolled
                                    // projection copied only name/sourceType/basePath, so a remote
                                    // collection arrived with IsStatic/IsPublic silently false — the mount
                                    // check would then 404 every legitimately-mounted remote collection.
                                    // Both are suppressed when false (WhenWritingDefault), so an absent
                                    // property means false, which is the safe value either way.
                                    IsStatic = e.TryGetProperty("isStatic", out var staticProp)
                                               && staticProp.ValueKind == JsonValueKind.True,
                                    IsPublic = e.TryGetProperty("isPublic", out var publicProp)
                                               && publicProp.ValueKind == JsonValueKind.True,
                                })
                                .ToArray(),
                            GetDataResponse { Data: IReadOnlyCollection<ContentCollectionConfig> direct } => direct,
                            _ => null
                        };
                        var sourceConfig = configs?.FirstOrDefault(c => c.Name == addressCollectionName);
                        if (sourceConfig == null) return null;
                        var built = sourceConfig with { Name = qualifiedCollectionName, Address = targetAddress };
                        collectionCache.TryAdd(qualifiedCollectionName, built);
                        return built;
                    });

            var serve = configObs.SelectMany(collectionConfig =>
            {
                if (collectionConfig == null)
                    return Observable.Return(Results.NotFound(
                        $"Content collection '{addressCollectionName}' not found at {resolution.Prefix}"));

                // 🚨 MOUNT CHECK — the owning node's collection must declare IsStatic. See the
                // pattern-1 note; the address shape needs the remote config first, so this runs
                // after the read gate rather than before it.
                if (!collectionConfig.IsStatic)
                    return Observable.Return(NotMounted(addressCollectionName));

                var portal = mainHub.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
                var portalContentService = portal.ServiceProvider.GetService<IContentService>();
                if (portalContentService is null)
                    return Observable.Return(Results.NotFound("Content service not configured"));

                portalContentService.AddConfiguration(collectionConfig);
                // Pure composition — collection resolution and the file read both run on the
                // collection's own IIoPool; ServeFile only shapes the (already-read) result.
                return portalContentService.GetCollection(qualifiedCollectionName)
                    .SelectMany(contentCollection => contentCollection == null
                        ? Observable.Return(Results.NotFound($"Content collection '{addressCollectionName}' not found"))
                        : ServeFile(context, contentCollection, addressFilePath, collectionConfig.IsPublic));
            });

            // 🚨 GATE FIRST — before the collection config is even looked up.
            //
            // Two reasons it has to be here and not deeper. (1) The config lookup is the only step
            // that ever touched access control (its GetDataRequest carries
            // [RequiresPermission(Read)]), and `collectionCache` SHORT-CIRCUITS it: once any
            // authorized caller populated the cache, every later caller — anonymous included —
            // skipped the permission-bearing hop entirely and the file was read locally from the
            // resolved BasePath. That cache hit was a standing bypass. (2) The bytes are read by
            // the PORTAL's content service straight off the resolved BasePath, so nothing
            // downstream consults the owning partition at all.
            //
            // The collection is mounted on `resolution.Prefix`, so that node is the owner floor;
            // appending the file path lets the resolver attribute the file to a deeper node when
            // the content mirrors the node tree (a paid lesson's media stays gated).
            return StaticContentGate
                .Authorize(mainHub, caller,
                    StaticContentGate.AddressOwnerCandidate(resolution.Prefix, addressFilePath), logger)
                .SelectMany(verdict => verdict == StaticContentGate.Verdict.Allowed
                    ? serve
                    : Observable.Return(StaticContentGate.DenyResult(verdict)));
        });
    }

    /// <summary>
    /// The <c>Cache-Control</c> value for a served file.
    ///
    /// <para>🚨 Only an explicitly-PUBLIC collection may be stored by a SHARED cache. Everything
    /// else is <c>private</c>: a CDN, corporate proxy or any intermediary that once saw an
    /// authorized fetch would otherwise keep serving that partition's file to callers this gate
    /// denies — the leak survives the fix (issue #587, point 4). Gated content stays cacheable in
    /// the caller's OWN browser but revalidates, so a revoked grant takes effect within
    /// <see cref="GatedCacheSeconds"/> instead of the 30 days <c>immutable</c> promised.</para>
    /// </summary>
    /// <param name="isPublic">Whether the owning collection declared <see cref="ContentCollectionConfig.IsPublic"/>.</param>
    /// <returns>The header value.</returns>
    internal static string CacheControlFor(bool isPublic) =>
        isPublic
            ? $"public, max-age={(int)TimeSpan.FromDays(30).TotalSeconds}, immutable"
            : $"private, max-age={GatedCacheSeconds}, must-revalidate";

    /// <summary>Browser-local lifetime of an access-controlled static file, in seconds.</summary>
    internal const int GatedCacheSeconds = 300;

    /// <summary>
    /// The response for a collection that exists but is not mounted on <c>/static</c>
    /// (<see cref="ContentCollectionConfig.IsStatic"/> not declared). 404, not 403: whether a
    /// collection is published on this route is a hosting decision, identical for every caller, so
    /// the answer must not vary with identity.
    /// </summary>
    /// <param name="collectionName">The collection that is not mounted.</param>
    /// <returns>The 404 result.</returns>
    internal static IResult NotMounted(string collectionName) =>
        Results.NotFound(
            $"Content collection '{collectionName}' is not served under /static. "
            + "Mount it explicitly (IsStatic) to publish it on this route.");

    /// <summary>
    /// Serves a file from a known collection (pattern: /static/{collection}/{filePath}).
    /// </summary>
    private static async Task<IResult> ServeFromCollection(
        HttpContext context,
        IMessageHub hub,
        string collectionName,
        string filePath,
        bool isPublic)
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

        // HTTP-boundary await of the composed pipeline — every leaf runs on the collection's pool.
        return await contentService.GetCollection(collectionName)
            .SelectMany(contentCollection => contentCollection == null
                ? Observable.Return(Results.NotFound($"Content collection '{collectionName}' not found"))
                : ServeFile(context, contentCollection, filePath, isPublic))
            .Take(1);
    }

    /// <summary>
    /// Serves a file from a content collection with proper caching headers. Composition — every
    /// read (including the small-file buffering for the ETag hash) runs on the collection's pool;
    /// this layer only shapes the HTTP result on the emissions.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="contentCollection">The resolved collection.</param>
    /// <param name="filePath">The file's path within the collection.</param>
    /// <param name="isPublic">Whether the collection is declared public — decides shared vs private caching.</param>
    private static IObservable<IResult> ServeFile(
        HttpContext context, ContentCollection contentCollection, string filePath, bool isPublic)
        => contentCollection.GetContent(filePath)
            .SelectMany(stream =>
            {
                if (stream == null)
                    return Observable.Return(Results.NotFound("File not found"));

                var contentType = GetContentType(filePath);
                var fileName = Path.GetFileName(filePath);

                // 🚨 INLINE unless a download was explicitly asked for.
                //
                // Passing a fileDownloadName to Results.File/Results.Stream ALWAYS emits
                // `Content-Disposition: attachment`, and a browser will not render an attachment
                // inline — a <video> or <img> pointing at it shows nothing. Every content-collection
                // file was served that way, so the AgenticEngineering cover's player stayed blank
                // while the bytes themselves were fine (9.89MB, decodable, HTTP 200).
                //
                // The `?download` query parameter is what asks for the attachment; without it the
                // name is omitted so the response is inline and the element renders.
                var isDownloadRequested = context.Request.Query.ContainsKey("download");
                var downloadName = DownloadNameFor(isDownloadRequested, fileName);

                // Small files: re-read fully buffered through the collection's pooled leaf so the
                // ETag hash never buffers on this thread.
                if (stream.CanSeek && stream.Length < 10_000_000) // Only compute hash for files smaller than 10MB
                {
                    stream.Dispose();
                    return contentCollection.GetContentBytes(filePath)
                        .Select(bytes =>
                        {
                            if (bytes is null)
                                return Results.NotFound("File not found");
                            context.Response.Headers.CacheControl = CacheControlFor(isPublic);
                            // Expires is the HTTP/1.0 twin of max-age; a shared cache that honours
                            // it must not be handed a 30-day promise for gated content either.
                            context.Response.Headers.Expires = (isPublic
                                    ? DateTime.UtcNow.AddDays(30)
                                    : DateTime.UtcNow.AddSeconds(GatedCacheSeconds))
                                .ToString("R");
                            var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes));
                            context.Response.Headers.ETag = $"\"{hash}\"";
                            // Range processing on this branch too: a file under the buffering
                            // threshold is still seekable media (the 9.89MB course intro is), and
                            // Safari refuses to play a video whose response advertises no
                            // Accept-Ranges. Chromium tolerates it, which is exactly why this
                            // survived a headless check while a real browser showed nothing.
                            return FileResultFor(bytes, filePath, isDownloadRequested);
                        });
                }

                // Large files stream straight through. They previously carried NO Cache-Control at
                // all, which lets a shared cache apply its own heuristic freshness — the same leak
                // as an explicit `public`. Classify them too.
                context.Response.Headers.CacheControl = CacheControlFor(isPublic);

                // Return the stream directly without loading it all into memory
                return Observable.Return(Results.Stream(
                    stream,
                    contentType,
                    downloadName,
                    enableRangeProcessing: true));
            });

    /// <summary>
    /// Decodes a collection name from a URL by replacing '~' back to '/'.
    /// Collection names with slashes (e.g., "Submissions@Microsoft/2026") are encoded
    /// with '~' in URLs to avoid path parsing issues.
    /// </summary>
    private static string DecodeCollectionName(string encodedName)
        => ContentCollectionsExtensions.DecodeCollectionName(encodedName);

    /// <summary>
    /// URL-decodes a content-collection file path captured from the <c>/static/{**path}</c>
    /// catch-all route parameter. ASP.NET Core leaves catch-all values percent-encoded (so the
    /// path's <c>/</c> separators survive as segment boundaries), which means each segment reaches
    /// this endpoint escaped exactly as <see cref="ContentCollections.ContentLayoutArea"/> emitted
    /// it (<c>Uri.EscapeDataString</c> per segment). Decoding per segment is the exact inverse: it
    /// restores spaces (<c>%20</c>) and UTF-8 escapes (e.g. <c>%C3%9C</c> → <c>Ü</c>) so the lookup
    /// matches the real stored path — without turning any escaped slash into a false separator.
    /// A plain ASCII path carries no escapes, so decoding is a no-op (never a double-decode of a
    /// literal <c>%</c>, which the encoder would have written as <c>%25</c>).
    /// </summary>
    internal static string DecodeContentPath(string encodedPath)
        => string.Join('/', encodedPath.Split('/').Select(Uri.UnescapeDataString));

}

