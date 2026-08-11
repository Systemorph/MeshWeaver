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
    /// Maps the MeshWeaver HTTP endpoints onto the application: the public build-asset route, the
    /// access-controlled content-file route, and the layout preview stub.
    ///
    /// <para>🚨 The two file routes are deliberately SEPARATE surfaces with opposite contracts
    /// (issue #587):</para>
    /// <list type="bullet">
    /// <item><c>/static/{mount}/{file}</c> — application BUILD OUTPUT only (icon SVGs, shipped doc
    ///   assets). No identity is resolved, no permission is evaluated, and the mesh is never
    ///   touched; responses are <c>public, immutable</c>. Everything served there is public by
    ///   construction.</item>
    /// <item><c>/api/content/{node}/{collection}/{file}</c> — MESH CONTENT. Every request is
    ///   evaluated by the owning node's hub via a <c>GetDataRequest</c> carrying
    ///   <c>[RequiresPermission(Read)]</c>; responses are <c>private</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="app">The web application to map the endpoints onto.</param>
    public static void MapMeshWeaver(this WebApplication app)
    {
        app.MapPublicStaticAssets(app.Services);
        app.MapContentFiles(app.Services);

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
    /// 🚨 THE PUBLIC BUILD-ASSET ROUTE — <c>/static/{mount}/{file}</c> (issue #587).
    ///
    /// <para>This endpoint performs NO access control, resolves NO identity and never touches the
    /// mesh. It serves exactly the <see cref="StaticAssetMount"/>s registered on the mesh — files
    /// compiled into a shipped MeshWeaver assembly (icon SVGs, the documentation package's images),
    /// which are public by construction and needed before any identity exists (the login page, the
    /// nav, every anonymous card). That is why the responses stay <c>public, immutable</c> and
    /// CDN-cacheable.</para>
    ///
    /// <para><b>What is deliberately NOT here any more.</b> Content collections. This route used to
    /// resolve any registered collection and stream its files with no authorization anywhere:
    /// <c>/static/storage/content/{node}/{file}</c> read the mesh-level backing store directly, so
    /// EVERY partition's uploads, attachments and PDFs were world-readable at a fully predictable
    /// URL, and <c>/static/{address}/{collection}/{file}</c> served any hub's collections without
    /// consulting that partition's policy. Content is not mounted here at all now — it is
    /// unreachable rather than merely denied, and it is served by
    /// <see cref="MapContentFiles"/> instead. A path whose first segment names no mount is 404,
    /// identically for every caller: whether something is published on this route is a hosting
    /// decision and must not vary with identity.</para>
    /// </summary>
    private static void MapPublicStaticAssets(this IEndpointRouteBuilder app, IServiceProvider services)
    {
        // Lazy resolution of IMessageHub to avoid circular dependency during startup.
        IReadOnlyDictionary<string, StaticAssetMount>? mounts = null;

        app.MapMethods("/static/{**path}", ["GET", "HEAD"], (HttpContext context, string path) =>
        {
            mounts ??= services.GetRequiredService<IMessageHub>().ServiceProvider
                .GetServices<StaticAssetMount>()
                .GroupBy(m => m.Segment, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            return ServeStaticAsset(context, mounts, path);
        });
    }

    /// <summary>
    /// Resolves one <c>/static</c> request against the registered build-asset mounts. Pure apart
    /// from reading the assembly's manifest resources (an in-memory, already-mapped read — no I/O
    /// pool, no hub, no scheduler hop).
    /// </summary>
    /// <param name="context">The current request (used for the download flag and response headers).</param>
    /// <param name="mounts">The registered mounts, keyed by their first path segment.</param>
    /// <param name="path">The catch-all route value (still percent-encoded).</param>
    /// <returns>The file, or 404 when the mount or the file does not exist.</returns>
    internal static IResult ServeStaticAsset(
        HttpContext context, IReadOnlyDictionary<string, StaticAssetMount> mounts, string? path)
    {
        // 🚨 Decode FIRST, then validate. ASP.NET Core normalizes `..` out of the request line, but
        // the catch-all value is still percent-encoded — `%2E%2E` survives normalization and only
        // becomes `..` here. Validating the raw value would wave a traversal straight through.
        var decoded = DecodeContentPath(path ?? "");
        var slash = decoded.IndexOf('/');
        if (slash <= 0)
            return Results.NotFound("Expected /static/{mount}/{file}.");

        var segment = decoded[..slash];
        var filePath = decoded[(slash + 1)..];
        if (!mounts.TryGetValue(segment, out var mount))
            return NotMounted(segment);

        byte[] bytes;
        using (var stream = mount.Open(filePath))
        {
            if (stream is null)
            {
                // 🚨 A missing NODE-TYPE ICON is served as a generated stand-in, never as a 404.
                // An icon path is embedded in rendered HTML and persisted on nodes, so a 404 here
                // surfaces as a broken image on a page that is otherwise fine — and it is a
                // permanent, silent defect: nothing retries, and a typo made once keeps shipping.
                // On this mesh 8 of the 35 referenced icons were 404 (including `bug.svg` from
                // core and `image.svg` from a shipped plugin), all of them rendering broken.
                //
                // The fallback is deliberately narrow: it applies ONLY to a missing FILE inside the
                // already-resolved icons mount. An unknown MOUNT still 404s above, so "is this
                // published on this route" stays a hosting decision that does not vary with the
                // request — and traversal is still rejected before we get here.
                if (!IsNodeTypeIcon(segment, filePath))
                    return Results.NotFound("File not found");

                bytes = GeneratedIcon.For(filePath);
            }
            else
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
        }

        // Public by construction — a build asset carries no user data, so a shared cache may keep
        // it. This is the ONLY route on which `public` is correct.
        context.Response.Headers.CacheControl = PublicCacheControl;
        context.Response.Headers.Expires = DateTime.UtcNow.Add(PublicCacheDuration).ToString("R");
        context.Response.Headers.ETag =
            $"\"{Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes))}\"";
        return FileResultFor(bytes, filePath, context.Request.Query.ContainsKey("download"));
    }

    /// <summary>The mount whose missing files are generated rather than 404'd (see the call site).</summary>
    private const string NodeTypeIconsSegment = "NodeTypeIcons";

    /// <summary>
    /// Whether this request is for a node-type icon, and therefore eligible for a generated
    /// stand-in.
    ///
    /// <para>🚨 <b><see cref="StaticAssetMount.Open"/> returns <c>null</c> for a REFUSED path as
    /// well as a missing one</b> — a traversal attempt and a typo are indistinguishable by its
    /// return value. So this must re-establish that the path was legitimate, or the fallback
    /// answers a refused request with <c>200</c> and quietly converts the traversal guard into a
    /// success. <c>StaticContentUnmountedTest.TraversalAttempts_AreRefused</c> caught exactly that
    /// on the first cut of this change.</para>
    ///
    /// <para>Node-type icons are a FLAT set of <c>.svg</c> files, so the eligible shape is exact:
    /// one path segment, no separators, safe by <see cref="StaticAssetMount.IsSafeRelativePath"/>,
    /// ending in <c>.svg</c>. Anything else — a nested path, a dot segment, another extension —
    /// falls through to the 404 it would have got before.</para>
    /// </summary>
    private static bool IsNodeTypeIcon(string segment, string filePath) =>
        string.Equals(segment, NodeTypeIconsSegment, StringComparison.OrdinalIgnoreCase)
        && filePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        && filePath.Length > ".svg".Length
        && !filePath.Contains('/', StringComparison.Ordinal)
        && !filePath.Contains('\\', StringComparison.Ordinal)
        && StaticAssetMount.IsSafeRelativePath(filePath);

    /// <summary>Test seam for <see cref="IsNodeTypeIcon"/> — the eligibility rule is the security
    /// boundary of the fallback, so it is pinned directly rather than only through HTTP.</summary>
    internal static bool IsNodeTypeIconForTest(string segment, string filePath) =>
        IsNodeTypeIcon(segment, filePath);

    /// <summary>How long a build asset stays fresh in any cache.</summary>
    internal static readonly TimeSpan PublicCacheDuration = TimeSpan.FromDays(30);

    /// <summary>
    /// The <c>Cache-Control</c> of a build asset. <c>public</c> is correct ONLY here — see
    /// <see cref="GatedCacheControl"/>.
    /// </summary>
    internal static string PublicCacheControl =>
        $"public, max-age={(int)PublicCacheDuration.TotalSeconds}, immutable";

    /// <summary>
    /// The <c>Cache-Control</c> of an access-controlled content file.
    ///
    /// <para>🚨 <c>private</c>, always. A CDN, corporate proxy or any intermediary that once saw an
    /// authorized fetch would otherwise keep replaying that partition's file to callers the owning
    /// hub denies — the leak would survive the fix (issue #587, point 4). The old code marked every
    /// content file <c>public, max-age=2592000, immutable</c>.</para>
    /// </summary>
    internal const string GatedCacheControl = "private, no-store";

    /// <summary>
    /// The response for a <c>/static</c> path whose first segment names no build-asset mount. 404,
    /// never 403: nothing about this decision depends on who is asking.
    /// </summary>
    /// <param name="segment">The unmatched first path segment.</param>
    /// <returns>The 404 result.</returns>
    internal static IResult NotMounted(string segment) =>
        Results.NotFound(
            $"'{segment}' is not a static build asset. /static serves application build output only; "
            + "mesh content is served by /api/content/{node}/{collection}/{file}.");

    /// <summary>
    /// 🚨 THE ACCESS-CONTROLLED CONTENT-FILE ROUTE —
    /// <c>/api/content/{node}/{collection}/{file}</c> (issue #587).
    ///
    /// <para>Every byte a content collection holds is served here. The owning node is resolved with
    /// <see cref="IPathResolver"/> (longest real-node prefix), and THAT node's hub is asked for the
    /// collection with a <c>GetDataRequest</c> — which carries <c>[RequiresPermission(Read)]</c>, so
    /// <c>AccessControlPipeline</c> makes exactly the decision an ordinary node read makes.
    /// Partition scoping, group expansion, public-read policies and per-subject denies all apply
    /// verbatim; there is no parallel rule set here to drift.</para>
    ///
    /// <para><b>Two shapes, one route.</b> When the first remainder segment names a collection on
    /// the resolved node it is the collection and the rest is the file
    /// (<c>/api/content/{node}/{collection}/{file}</c>); otherwise the whole remainder is a path in
    /// the node's default <c>content</c> collection (<c>/api/content/{node}/{file…}</c>). The second
    /// shape is the direct replacement for the old <c>/static/storage/content/{node}/{file…}</c>:
    /// the mesh-level store lays every node's content out at <c>content/{nodePath}/…</c>, so
    /// resolving the node and reading its own collection reaches the same bytes — with the owner's
    /// permission check in front of them, which the store shape never had.</para>
    ///
    /// <para><b>No config cache.</b> The <c>GetDataRequest</c> IS the permission check, so caching
    /// its result and short-circuiting later requests was a standing bypass: once any authorized
    /// caller warmed the cache, every later caller — anonymous included — skipped the
    /// permission-bearing hop and the file was read locally off the resolved BasePath.</para>
    /// </summary>
    private static void MapContentFiles(this IEndpointRouteBuilder app, IServiceProvider services)
    {
        // Lazy resolution of IMessageHub to avoid circular dependency during startup
        IMessageHub? mainHub = null;

        app.MapMethods(ContentCollectionsExtensions.ContentFileRoutePrefix + "/{**path}", ["GET", "HEAD"],
            (HttpContext context, string path) =>
        {
            mainHub ??= services.GetRequiredService<IMessageHub>();

            if (string.IsNullOrEmpty(path))
                return Task.FromResult(Results.NotFound("Path is required"));

            // 🚨 Resolve the caller SYNCHRONOUSLY, before any scheduler hop. AccessContext is an
            // AsyncLocal that does not survive the Rx hops below, and concurrent requests carry
            // different users — so the identity is captured here per request and threaded through
            // explicitly, never re-read from the ambient service downstream.
            var caller = ResolveContentCaller(context, mainHub);

            // Compose the entire endpoint as IObservable<IResult>; the HTTP framework
            // boundary mandates Task<IResult>, so we ToTask once at the very end.
            // No await on hub round-trips — chain via SelectMany; deadlock surface
            // (await pathResolver.ResolvePath / hub.Observe) eliminated.
            return ResolveContentFile(context, mainHub, path, caller)
                // 🚨 A DENIAL IS NOT AN ERROR — it must be indistinguishable from absence.
                // The owning hub refuses a GetDataRequest the caller lacks Read on by posting a
                // DeliveryFailure, which hub.Observe surfaces through onError as a
                // DeliveryFailureException. That is the ONLY way a denial arrives here: the
                // documented "config reads back null ⇒ 404" path never runs for one, because the
                // stream faults instead of delivering null. Catching it as a generic error gave
                // BOTH of the following to any unauthenticated caller:
                //
                //   • a 500-vs-404 ORACLE — "500" meant the node exists and you are denied, "404"
                //     meant no such file, so a prober could map every private partition by URL
                //     alone, which is the enumeration half of the #587 hole on the route the
                //     content MOVED to; and
                //   • the denial text itself, verbatim, in the problem body:
                //     "Access denied: user 'Anonymous' lacks Read permission on 'Doc'" — the
                //     permission model, the principal and the node's existence, unauthenticated.
                //
                // So a refused read answers exactly like a missing one: 404, no detail. The
                // AccessControlPipeline has already logged the real reason server-side, which is
                // where it belongs. Genuine faults (transport, IO) keep Problem — they are not
                // caller-triggerable this way — but never echo the exception text.
                .Catch<IResult, Exception>(ex => Observable.Return(
                    ex is DeliveryFailureException
                        ? Results.NotFound()
                        : Results.Problem("Error retrieving content")))
                .FirstAsync()
                .ToTask(context.RequestAborted);
        });
    }

    /// <summary>
    /// The identity of a content-file request. NEVER null — an unauthenticated caller resolves to
    /// the well-known Anonymous context, whose permissions are exactly the Anonymous grants.
    ///
    /// <para><c>UserContextMiddleware</c> runs for this route and stamps the fully-resolved identity
    /// on the mesh-wide <c>AccessService</c>, including the Bearer-token path that a claims
    /// principal alone cannot see. Prefer that; fall back to resolving the claims principal directly
    /// so a host that does not run the middleware still names the caller correctly rather than
    /// posting with no context (which the never-null PostPipeline guard refuses — invisible at one
    /// replica, a 500 at two, #694).</para>
    /// </summary>
    private static AccessContext ResolveContentCaller(HttpContext context, IMessageHub mainHub)
    {
        var accessService = context.RequestServices.GetService<PortalApplication>()?.Hub
                                .ServiceProvider.GetService<AccessService>()
                            ?? mainHub.ServiceProvider.GetService<AccessService>();
        var ambient = accessService?.Context ?? accessService?.CircuitContext;
        return ambient is not null && !string.IsNullOrEmpty(ambient.ObjectId)
            ? ambient
            : UserContextMiddleware.ResolveHttpCaller(context.User, mainHub.ServiceProvider);
    }

    private static IObservable<IResult> ResolveContentFile(
        HttpContext context,
        IMessageHub mainHub,
        string path,
        AccessContext caller)
    {
        // 🚨 Decode FIRST, then validate — `%2E%2E` survives the server's URL normalization and only
        // becomes `..` here. FileSystemStreamProvider resolves a collection-relative path with a
        // bare Path.Combine, so an un-guarded `..` would read outside the collection's BasePath
        // (i.e. another partition's files) while this route attributed the request to the node whose
        // grant the caller does hold.
        var decodedPath = DecodeContentPath(path);
        if (!StaticAssetMount.IsSafeRelativePath(decodedPath))
            return Observable.Return(Results.NotFound("Invalid content path"));

        // 🚨 ONE resolution, shared. Which segments name the node and which name the collection is
        // decided by ContentFileResolver — the same reading every server-side content read uses.
        // A second, private copy of this logic is what let the deck export ask for a collection
        // named after the partition and silently print blank images (issue #990).
        return ContentFileResolver.Resolve(mainHub, decodedPath, caller).SelectMany(result =>
        {
            if (result.Resolution is not { } resolution)
                return Observable.Return(Results.NotFound(result.Reason));

            var sourceConfig = resolution.Collection;

            // 🚨 MOUNT CHECK, default-closed. A collection is servable by URL only when it
            // declares it (ContentCollectionConfig.IsStatic). The flag existed but was read
            // NOWHERE — set at two sites, consulted at zero — so every collection registered
            // anywhere on the mesh was fetchable by URL. Not declared ⇒ 404, for every
            // caller alike (publishing is a hosting decision, not an access one).
            if (!sourceConfig.IsStatic)
                return Observable.Return(NotServable(sourceConfig.Name));

            // The portal hub is where the resolved config is cached and the bytes are read.
            // Fall back to the mesh hub for hosts that do not run the Blazor portal (the
            // sidecar, tests) — the access decision has already been made above.
            var portal = mainHub.ServiceProvider.GetService<PortalApplication>()?.Hub ?? mainHub;
            var portalContentService = portal.ServiceProvider.GetService<IContentService>();
            if (portalContentService is null)
                return Observable.Return(Results.NotFound("Content service not configured"));

            portalContentService.AddConfiguration(resolution.QualifiedConfig);
            // Pure composition — collection resolution and the file read both run on the
            // collection's own IIoPool; ServeFile only shapes the (already-read) result.
            return portalContentService.GetCollection(resolution.QualifiedName)
                .SelectMany(contentCollection => contentCollection == null
                    ? Observable.Return(Results.NotFound($"Content collection '{sourceConfig.Name}' not found"))
                    : ServeFile(context, contentCollection, resolution.FilePath));
        });
    }

    /// <summary>
    /// The response for a collection that exists but does not declare itself servable by URL
    /// (<see cref="ContentCollectionConfig.IsStatic"/>). 404, not 403 — identical for every caller.
    /// </summary>
    /// <param name="collectionName">The collection that is not published.</param>
    /// <returns>The 404 result.</returns>
    internal static IResult NotServable(string collectionName) =>
        Results.NotFound(
            $"Content collection '{collectionName}' is not served over HTTP. "
            + "Declare IsStatic on it to publish it on the content route.");

    /// <summary>
    /// Serves a file from a content collection with proper caching headers. Composition — every
    /// read (including the small-file buffering for the ETag hash) runs on the collection's pool;
    /// this layer only shapes the HTTP result on the emissions.
    /// </summary>
    private static IObservable<IResult> ServeFile(HttpContext context, ContentCollection contentCollection, string filePath)
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
                            // 🚨 private, never public (issue #587, point 4). This response is
                            // access-controlled: a CDN, corporate proxy or any intermediary that
                            // once stored an authorized fetch would keep replaying that partition's
                            // file to callers the owning hub denies — the leak would survive the
                            // fix. The old value was `public, max-age=2592000, immutable`.
                            context.Response.Headers.CacheControl = GatedCacheControl;
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
                context.Response.Headers.CacheControl = GatedCacheControl;

                // Return the stream directly without loading it all into memory
                return Observable.Return(Results.Stream(
                    stream,
                    contentType,
                    downloadName,
                    enableRangeProcessing: true));
            });

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

