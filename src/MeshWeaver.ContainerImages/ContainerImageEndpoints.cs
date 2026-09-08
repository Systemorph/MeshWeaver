using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// The OCI Distribution pull surface, served from the mesh: <c>GET /v2/</c>, the bearer token
/// exchange at <c>GET /v2/token</c>, <c>…/manifests/{reference}</c>, <c>…/blobs/{digest}</c> and
/// <c>…/tags/list</c>, proxied to the upstream registry with the mirror's own credential while the
/// CALLER authenticates against memex.
///
/// <para><b>Why this exists</b> (see <c>Doc/Architecture/ContainerRegistryInMemex</c>): the fleet
/// carried an <c>ACR_USERNAME</c>/<c>ACR_PASSWORD</c> pair in every satellite repository purely so
/// its CI could <c>docker login</c> and pull the tester image — alongside the memex registry token
/// those repositories already held for plugin bundles. This collapses that to one credential,
/// held here.</para>
///
/// <para><b>Pull only, deliberately.</b> No push, no upload, no delete. Pushes keep going to the
/// upstream, so CD is unchanged and this can be switched off without a migration.</para>
///
/// <para>🚨 <b>This mirror must never serve the image that boots its own portal.</b> Kubernetes
/// pulls before any MeshWeaver process exists, so a cluster pointing at its own mesh for its boot
/// image cannot start. Serving OTHER installations, and serving CI, has no such circularity — the
/// constraint is per-instance, not global.</para>
/// </summary>
public static class ContainerImageEndpoints
{
    /// <summary>Route prefix mandated by the OCI Distribution Specification.</summary>
    public const string RoutePrefix = "/v2";

    /// <summary>
    /// The token endpoint, relative to <see cref="RoutePrefix"/>. This is the <c>realm</c> the
    /// bearer challenge NAMES, so the two must never drift: a challenge pointing at a route
    /// nothing serves turns every <c>docker pull</c> into "401, fetch the realm, 404, give up".
    /// </summary>
    public const string TokenRoute = "/token";

    /// <summary>The API version every OCI client expects the version probe to declare.</summary>
    public const string ApiVersionHeader = "Docker-Distribution-Api-Version";

    /// <summary>Value of <see cref="ApiVersionHeader"/>.</summary>
    public const string ApiVersion = "registry/2.0";

    /// <summary>
    /// Lifetime advertised on an issued token, in seconds. Short on purpose: the token IS the
    /// caller's own instance key (see <see cref="IssueToken"/>), so a client re-presenting it
    /// re-runs <see cref="IContainerImageAuthenticator"/> — which is what makes revoking a key
    /// take effect in minutes rather than whenever a minted credential happened to expire.
    /// </summary>
    public const int TokenLifetimeSeconds = 300;

    /// <summary>
    /// Names where the bytes came from, on every pull response: <c>hit</c> (the read-through
    /// cache, upstream not contacted), <c>miss</c> (fetched from the upstream and stored),
    /// <c>bypass</c> (fetched and deliberately not stored — a tag, a range, an unsupported digest
    /// algorithm) or <c>disabled</c> (no cache is configured).
    ///
    /// <para>Diagnostic, not contractual: an operator reading a log or a header can tell which of
    /// the four happened without inferring it from timings.</para>
    /// </summary>
    public const string CacheStatusHeader = "X-MeshWeaver-Cache";

    /// <summary>Set by the auth gate so handlers can name the caller in logs and records.</summary>
    private const string CallerItemKey = "ContainerImages.Caller";

    private const string ManifestsKind = "manifests";

    private const string BlobsKind = "blobs";

    /// <summary>
    /// Maps the pull surface. <c>AllowAnonymous</c> at the ASP.NET layer for the same reason the
    /// plugin registry does it — callers are INSTANCES and CI jobs, not signed-in users — with the
    /// bearer gate below doing the real work.
    /// </summary>
    /// <param name="endpoints">The route builder to map onto.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapContainerImages(this IEndpointRouteBuilder endpoints)
    {
        // 🚨 A cache directory configured with no ContainerBlobCache registered would proxy every
        // pull while the configuration says it caches — a silently half-configured service, which
        // this file refuses everywhere else. Fail at STARTUP, naming the fix, rather than serving
        // something that looks right.
        var configured = endpoints.ServiceProvider
            .GetService<IOptions<ContainerImageOptions>>()?.Value;
        if (configured?.CacheDirectory is { Length: > 0 }
            && endpoints.ServiceProvider.GetService<ContainerBlobCache>() is null)
            throw new InvalidOperationException(
                $"{ContainerImageOptions.SectionName}:CacheDirectory is set but "
                + $"{nameof(ContainerBlobCache)} is not registered. Call "
                + $"services.{nameof(ContainerImageServiceExtensions.AddContainerImageMirror)}() "
                + "so the read-through cache actually exists, or clear the setting.");

        // 🚨 The token endpoint sits OUTSIDE the challenge filter, in its own group over the same
        // prefix. The filter answers "not authenticated" with a challenge that NAMES this route —
        // so putting this route behind it makes an unauthenticated client loop: challenge → token
        // → challenge → token. A token endpoint authenticates ITSELF and refuses with a bare 401.
        // Route precedence puts the literal `/v2/token` ahead of the `/v2/{**rest}` catch-all, so
        // this wins without depending on registration order.
        var tokenGroup = endpoints.MapGroup(RoutePrefix).AllowAnonymous();
        tokenGroup.MapGet(TokenRoute, (HttpContext http, CancellationToken ct) => IssueToken(http, ct));

        var group = endpoints.MapGroup(RoutePrefix).AllowAnonymous();

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var client = http.RequestServices.GetRequiredService<UpstreamRegistryClient>();
            var logger = Logger(http);

            // Unconfigured is 404 on everything, not 401 and not a partial service. A mirror
            // without a credential cannot serve a single byte, and saying "unauthorised" would
            // send an operator hunting for a token problem that does not exist.
            if (!client.IsConfigured)
            {
                logger?.LogDebug(
                    "Container registry mirror: {Path} refused — {Section}:Upstream/Username/Password "
                    + "are not all set, so the mirror is off.",
                    http.Request.Path, ContainerImageOptions.SectionName);
                return Results.NotFound();
            }

            var caller = await Authenticate(http, ct: http.RequestAborted);
            if (caller is null)
                return Challenge(http);

            http.Items[CallerItemKey] = caller;
            return await next(ctx);
        });

        // The spec's version probe. A client hits this first and reads the challenge from it, so
        // it must answer 200 (authenticated) or 401-with-challenge — never 404.
        group.MapMethods("/", PullMethods, (HttpContext http) =>
        {
            http.Response.Headers[ApiVersionHeader] = ApiVersion;
            return Results.Ok(new { });
        });

        group.MapMethods(
            "/{**rest}", PullMethods, (HttpContext http, CancellationToken ct) => Serve(http, ct));
        return endpoints;
    }

    /// <summary>
    /// The two methods a PULL uses. <c>HEAD</c> is how containerd checks a manifest before it
    /// downloads one, and a registry that answers 405 to it makes every such client take the slow
    /// fallback path. Nothing else is mapped: this is a pull surface, and <c>PUT</c>/<c>PATCH</c>/
    /// <c>POST</c>/<c>DELETE</c> are refused by never existing rather than by being handled.
    /// </summary>
    private static readonly string[] PullMethods = ["GET", "HEAD"];

    /// <summary>
    /// Resolves the caller through <see cref="IContainerImageAuthenticator"/>, normalising the
    /// <c>Authorization</c> header to the single bearer shape the seam has to speak.
    /// </summary>
    private static async Task<string?> Authenticate(HttpContext http, CancellationToken ct)
    {
        var authenticator = http.RequestServices.GetRequiredService<IContainerImageAuthenticator>();
        var header = http.Request.Headers.Authorization.ToString();
        // Both shapes a registry client uses reduce to one secret; an unreadable header is passed
        // through verbatim so the seam — not this file — stays the authority on what counts.
        var normalized = RegistryCredential.TryReadSecret(header) is { } secret
            ? RegistryCredential.AsBearerHeader(secret)
            : header;
        var logger = Logger(http);
        // 🚨 ObserveCompletion, never .ToTask() — a Task completed inside an Rx pipeline
        // resumes its awaiter INLINE on the signalling thread, still inside Rx's trampoline,
        // and everything the continuation then does inherits that scheduler.
        return await authenticator
            .Authenticate(normalized, ct)
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Container registry mirror: authentication for {Path} faulted after the "
                    + "request had already been answered", http.Request.Path),
                ct);
    }

    /// <summary>
    /// The token exchange the bearer challenge sends a client to:
    /// <c>GET /v2/token?service=…&amp;scope=repository:&lt;name&gt;:pull</c> carrying
    /// <c>Basic base64(user:key)</c>, answered with a bearer the client then presents on every
    /// pull.
    ///
    /// <para>🚨 <b>The mirror does not MINT a credential in v1 — the bearer it hands back IS the
    /// caller's own instance key.</b> That is a deliberate choice, not a shortcut: a minted token
    /// would stay valid for its full lifetime after the key behind it was revoked, and it would
    /// make the mirror a second issuer of credentials for an identity it does not own. Echoing the
    /// key means every subsequent request re-runs
    /// <see cref="IContainerImageAuthenticator"/> — revocation takes effect at the next token
    /// exchange, which <see cref="TokenLifetimeSeconds"/> keeps minutes away. It also means the
    /// mirror stores no token state at all, so there is nothing to leak, expire or replicate.</para>
    ///
    /// <para>The <c>scope</c> query parameter is deliberately NOT enforced here. The pull path
    /// checks the repository allowlist on every request against the repository it actually
    /// forwards, so a token scoped by this endpoint would be a SECOND opinion about what may be
    /// served — and two opinions is how one of them ends up wrong.</para>
    /// </summary>
    private static async Task<IResult> IssueToken(HttpContext http, CancellationToken ct)
    {
        var client = http.RequestServices.GetRequiredService<UpstreamRegistryClient>();
        if (!client.IsConfigured)
            return Results.NotFound();

        var secret = RegistryCredential.TryReadSecret(http.Request.Headers.Authorization.ToString());
        if (secret is null)
            return RefuseToken(http);

        var caller = await Authenticate(http, ct);
        if (caller is null)
            return RefuseToken(http);

        Logger(http)?.LogDebug(
            "Container registry mirror: issued a pull token to {Caller} for scope {Scope}",
            caller, http.Request.Query["scope"].ToString());

        // `token` is the OCI Distribution field; `access_token` is the OAuth2 spelling ACR and
        // Docker Hub both also return. Clients read one or the other, so emit both.
        return Results.Json(new
        {
            token = secret,
            access_token = secret,
            expires_in = TokenLifetimeSeconds,
            issued_at = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// <summary>
    /// A refused token exchange: 401 WITHOUT a bearer challenge. Challenging here would name this
    /// very route and loop the client.
    /// </summary>
    private static IResult RefuseToken(HttpContext http)
    {
        http.Response.Headers[ApiVersionHeader] = ApiVersion;
        return Results.Json(
            new { errors = new[] { new { code = "UNAUTHORIZED", message = "instance key required" } } },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// The bearer challenge every OCI client expects before it will present a credential. Naming
    /// the realm is what makes `docker pull` fetch a token rather than simply failing — and the
    /// realm names <see cref="TokenRoute"/>, which <see cref="MapContainerImages"/> serves.
    /// </summary>
    private static IResult Challenge(HttpContext http)
    {
        var realm = $"{http.Request.Scheme}://{http.Request.Host}{RoutePrefix}{TokenRoute}";
        var challenge = $"Bearer realm=\"{realm}\",service=\"{http.Request.Host}\"";
        // The scope, when the request names a repository: a client uses it verbatim to ask for a
        // token, and the shape matches the one ACR emits (the live reference for this handshake).
        if (http.Request.RouteValues["rest"] is string rest
            && RegistryRoute.TryParse(rest, out var route))
            challenge += $",scope=\"repository:{route.Repository}:pull\"";
        http.Response.Headers["WWW-Authenticate"] = challenge;
        http.Response.Headers[ApiVersionHeader] = ApiVersion;
        return Results.Json(
            new { errors = new[] { new { code = "UNAUTHORIZED", message = "instance key required" } } },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// The caller's query string, forwarded on the <c>tags/list</c> route ONLY.
    ///
    /// <para><c>tags/list</c> is the one pull route the Distribution API paginates: a registry
    /// answers the first page and names the next in a RELATIVE <c>Link</c> header
    /// (<c>&lt;/v2/{name}/tags/list?n=100&amp;last=…&gt;; rel="next"</c>), and ACR pages at 100 tags
    /// sorted lexically — so a mirror that dropped <c>?n=</c>/<c>?last=</c> answered every request
    /// with the OLDEST hundred tags and could never be walked past them. The self-updater lists
    /// tags through this route (#3353); against a repository with 500 builds it would have found
    /// nothing newer than what it runs, forever, and printed the up-to-date sentence. The
    /// <c>Link</c> header is forwarded by <see cref="UpstreamPassthroughResult"/> and, being
    /// relative, resolves against the mirror's own host.</para>
    ///
    /// <para>A manifest or blob route never carries a query the upstream would honour, so nothing
    /// is forwarded there: a query is not part of a content address and must not reach a cache
    /// key or an allowlist check by the back door.</para>
    /// </summary>
    private static string TagsQuery(HttpContext http, RegistryRoute route) =>
        route.Kind == "tags" && http.Request.QueryString.HasValue
            ? http.Request.QueryString.Value ?? string.Empty
            : string.Empty;

    private static async Task<IResult> Serve(HttpContext http, CancellationToken ct)
    {
        var client = http.RequestServices.GetRequiredService<UpstreamRegistryClient>();
        var logger = Logger(http);

        var rest = (string?)http.Request.RouteValues["rest"] ?? string.Empty;
        if (!RegistryRoute.TryParse(rest, out var route))
            return Results.NotFound();

        // 🚨 The allowlist is the difference between a mirror and an open read proxy for the whole
        // upstream. Empty means NONE — and it is checked BEFORE the cache, so a repository the
        // mirror does not serve is refused whether or not its bytes happen to be resident.
        var options = http.RequestServices.GetRequiredService<IOptions<ContainerImageOptions>>().Value;
        if (!options.Repositories.Contains(route.Repository, StringComparer.Ordinal))
        {
            logger?.LogDebug(
                "Container registry mirror: repository {Repository} is not in {Section}:Repositories",
                route.Repository, ContainerImageOptions.SectionName);
            return Results.NotFound();
        }

        // 🚨 The bound on the layer transfer. IoPoolNames.Blob, not Http: `Http` is capped at 16
        // for short API round-trips, and one 300 MB layer parked in that pool for a minute would
        // starve every plugin-catalog and registration call the portal makes. `Blob` IS the
        // large-async-binary resource class (cap 128), which is exactly what a layer transfer is.
        var pool = ContainerImagePools.Resolve(http.RequestServices, IoPoolNames.Blob);
        var cache = http.RequestServices.GetService<ContainerBlobCache>();
        var isHead = HttpMethods.IsHead(http.Request.Method);
        var range = http.Request.Headers.Range.ToString();

        // 🚨 Only a DIGEST is a cache key. A tag is mutable — caching one would serve a stale
        // image forever, and the symptom would look like a stale build rather than a stale cache.
        // A RANGE request is excluded too: a partial body cannot be verified against the whole
        // body's digest, and an unverified entry is worse than no entry.
        var key = route.Kind is ManifestsKind or BlobsKind
                  && ContainerBlobCache.IsSupportedDigest(route.Reference)
            ? route.Reference
            : null;
        var cacheable = cache is { IsEnabled: true } && key is not null
                        && string.IsNullOrEmpty(range);

        if (cacheable)
        {
            var hit = await cache!.Open(key!)
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger?.LogWarning(ex,
                        "Container registry mirror: the cache lookup for {Path} faulted after the "
                        + "request had already been answered", http.Request.Path),
                    ct);
            if (hit is not null)
            {
                // Off the response path, with an explicit error arm: LRU bookkeeping must never be
                // able to fail a pull that has already been answered correctly.
                cache.Touch(key!).Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(ex,
                        "Container registry mirror: could not refresh the cache timestamp for "
                        + "{Digest}; eviction order for this entry may be stale.", key));
                logger?.LogDebug(
                    "Container registry mirror: served {Digest} from the cache — the upstream was "
                    + "not contacted.", key);
                http.Response.Headers[CacheStatusHeader] = "hit";
                return new CachedContentResult(hit, pool, isHead);
            }
        }

        http.Response.Headers[CacheStatusHeader] =
            cache is not { IsEnabled: true } ? "disabled" : cacheable ? "miss" : "bypass";

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.OpenAsync(
                isHead ? HttpMethod.Head : HttpMethod.Get, route.Repository,
                "/v2/" + rest + TagsQuery(http, route), range, ct);
        }
        catch (UpstreamUnreachableException ex)
        {
            // 🚨 504, and emphatically NOT 404. "I could not fetch it" and "it does not exist" are
            // different answers, and a consumer that cannot tell them apart writes off an artefact
            // that is still there — or retries one that is genuinely gone. The cache could not
            // help here: nothing matching was resident, which is why we tried the upstream at all.
            logger?.LogWarning(ex,
                "Container registry mirror: {Path} could not be fetched — the upstream is "
                + "unreachable. Answering 504 (unavailable), never 404 (absent).",
                http.Request.Path);
            return UpstreamError(
                http, StatusCodes.Status504GatewayTimeout, "UPSTREAM_UNAVAILABLE",
                "the upstream registry could not be reached; this is not a statement about "
                + "whether the requested content exists");
        }
        catch (UpstreamRegistryException ex)
        {
            // The mirror's own credential failed — that is a 502, never a 401. A 401 here would
            // tell the caller to fix ITS token, which is not the broken thing.
            logger?.LogWarning("Container registry mirror: upstream refused {Path} — {Reason}",
                http.Request.Path, ex.Message);
            return UpstreamError(
                http, StatusCodes.Status502BadGateway, "UPSTREAM_UNAUTHORIZED",
                "the mirror's own upstream credential was refused; the caller's token is not the "
                + "problem");
        }

        // A manifest is read whole when it must be RECORDED or CACHED — both need the bytes, and
        // reading once serves both. Bounded by MaxRecordedManifestBytes; a blob never takes this
        // path under any configuration.
        var wantsRecord = route.Kind == ManifestsKind && options.ImageRoot is { Length: > 0 };
        var wantsManifestCache = cacheable && route.Kind == ManifestsKind;
        if (!isHead && (wantsRecord || wantsManifestCache)
            && TryReadManifest(upstream, options, ct, out var manifest))
        {
            byte[] body;
            try
            {
                body = await manifest;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // 🚨 The upstream connection died mid-manifest. Nothing has been written to the
                // caller yet, so this is a clean 504 — the upstream stopped answering, which is
                // exactly "unreachable" and must not be reported as absent. And the response MUST
                // be disposed here: ownership normally passes to UpstreamPassthroughResult, which
                // is never constructed on this path, so returning without disposing leaks the
                // connection.
                upstream.Dispose();
                logger?.LogWarning(ex,
                    "Container registry mirror: reading the manifest for {Path} failed mid-body",
                    http.Request.Path);
                return UpstreamError(
                    http, StatusCodes.Status504GatewayTimeout, "UPSTREAM_UNAVAILABLE",
                    "the upstream registry stopped answering mid-manifest");
            }

            if (wantsRecord)
                Record(http, client.Upstream, route, body, options.ImageRoot!, logger);
            if (wantsManifestCache)
                StoreManifest(cache!, key!, upstream, body, route, logger);
            // Serving the bytes we already hold, rather than re-reading a consumed stream.
            return new UpstreamPassthroughResult(upstream, pool, body);
        }

        // 🚨 A HEAD carries no body, so there is nothing to hash and nothing to store: caching one
        // would file an empty entry under a real digest, which is the worst outcome this cache can
        // have. Cacheable BLOB responses tee; everything else is the plain passthrough.
        if (cacheable && !isHead && route.Kind == BlobsKind && upstream.IsSuccessStatusCode)
        {
            var mediaType = upstream.Content.Headers.ContentType?.ToString();
            return new UpstreamPassthroughResult(
                upstream, pool, downstream => cache!.BeginFill(key!, mediaType, downstream));
        }

        return new UpstreamPassthroughResult(upstream, pool);
    }

    /// <summary>
    /// An OCI-shaped error the mirror ITSELF produces — a failure to reach or use the upstream,
    /// never a statement about the requested content.
    /// </summary>
    private static IResult UpstreamError(HttpContext http, int status, string code, string message)
    {
        http.Response.Headers[ApiVersionHeader] = ApiVersion;
        return Results.Json(
            new { errors = new[] { new { code, message } } }, statusCode: status);
    }

    /// <summary>
    /// Stores a manifest the mirror already holds in memory, off the response path with an
    /// explicit error arm. A store that fails costs a re-fetch next time and nothing else.
    /// </summary>
    private static void StoreManifest(
        ContainerBlobCache cache, string digest, HttpResponseMessage upstream, byte[] body,
        RegistryRoute route, ILogger? logger)
    {
        if (!upstream.IsSuccessStatusCode)
            return;
        cache.Store(digest, upstream.Content.Headers.ContentType?.ToString(), body)
            .Subscribe(
                stored => logger?.LogDebug(
                    "Container registry mirror: manifest {Repository}@{Digest} {Outcome}.",
                    route.Repository, digest, stored ? "cached" : "was not cached"),
                ex => logger?.LogWarning(ex,
                    "Container registry mirror: serving manifest {Repository}@{Digest} succeeded "
                    + "but caching it failed. The pull is unaffected; the next one re-fetches.",
                    route.Repository, digest));
    }

    /// <summary>
    /// Whether this response is a MANIFEST small enough to hold in memory — for recording its
    /// closure, for caching it, or both.
    ///
    /// <para>🚨 The size test is on the upstream's declared <c>Content-Length</c>, BEFORE
    /// anything is read — so a body that would not fit is never partially consumed, and streams
    /// untouched. And the caller only reaches this for manifests: a blob is never buffered under
    /// any configuration, which is the difference between recording an image and OOMing the
    /// portal.</para>
    /// </summary>
    private static bool TryReadManifest(
        HttpResponseMessage upstream,
        ContainerImageOptions options,
        CancellationToken ct,
        out Task<byte[]> body)
    {
        body = Task.FromResult<byte[]>([]);
        if (!upstream.IsSuccessStatusCode
            || upstream.Content.Headers.ContentLength is not { } length
            || length <= 0
            || length > options.MaxRecordedManifestBytes)
            return false;
        body = upstream.Content.ReadAsByteArrayAsync(ct);
        return true;
    }

    /// <summary>
    /// Records what the mirror just served as a <see cref="ContainerImageRecord"/> node.
    ///
    /// <para>🚨 OFF the response path, and never able to fail it. The write is a cold observable
    /// subscribed here with an explicit error arm — it runs on <c>Subscribe</c>, not on call, and
    /// a failure is a warning naming the root, never an error handed to a <c>docker pull</c>.
    /// Recording is observational: the mirror's contract is to serve the right bytes.</para>
    /// </summary>
    private static void Record(
        HttpContext http, string registry, RegistryRoute route, byte[] body, string imageRoot,
        ILogger? logger)
    {
        var hub = http.RequestServices.GetService<IMessageHub>();
        if (hub is null)
        {
            logger?.LogDebug(
                "Container registry mirror: no message hub in scope, so {Repository}:{Reference} "
                + "was served but not recorded.", route.Repository, route.Reference);
            return;
        }

        var record = ContainerImageCatalog.Describe(
            registry, route.Repository, route.Reference, body,
            http.Items.TryGetValue(CallerItemKey, out var caller) ? caller as string : null,
            DateTimeOffset.UtcNow);
        if (record is null)
        {
            // Not an OCI/Docker v2 manifest — an attestation, a signature artifact, a v1 manifest.
            // Served, not modelled. Recording a half-parsed closure would be worse than none.
            logger?.LogDebug(
                "Container registry mirror: {Repository}:{Reference} is not a manifest shape this "
                + "mirror models, so it was served but not recorded.",
                route.Repository, route.Reference);
            return;
        }

        ContainerImageCatalog.Record(hub, imageRoot, record)
            .Subscribe(
                node => logger?.LogDebug(
                    "Container registry mirror: recorded {Repository}:{Reference} at {Path} "
                    + "({Digest}, {Layers} layer(s), {Platforms} platform(s))",
                    route.Repository, route.Reference, node.Path, record.Digest,
                    record.Layers.Length, record.Platforms.Length),
                ex => logger?.LogWarning(ex,
                    "Container registry mirror: serving {Repository}:{Reference} succeeded but "
                    + "recording it under {ImageRoot} failed. The pull is unaffected; the closure "
                    + "for this image is simply not in the mesh. Check that {ImageRoot} exists.",
                    route.Repository, route.Reference, imageRoot, imageRoot));
    }

    private static ILogger? Logger(HttpContext http) =>
        http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ContainerImageEndpoints));
}
