using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContainerRegistry;

/// <summary>
/// The OCI Distribution pull surface, served from the mesh: <c>GET /v2/</c>,
/// <c>…/manifests/{reference}</c>, <c>…/blobs/{digest}</c> and <c>…/tags/list</c>, proxied to the
/// upstream registry with the mirror's own credential while the CALLER authenticates against
/// memex.
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
public static class ContainerRegistryEndpoints
{
    /// <summary>Route prefix mandated by the OCI Distribution Specification.</summary>
    public const string RoutePrefix = "/v2";

    /// <summary>Set by the auth gate so handlers can name the caller in logs.</summary>
    private const string CallerItemKey = "ContainerRegistry.Caller";

    /// <summary>
    /// Maps the pull surface. <c>AllowAnonymous</c> at the ASP.NET layer for the same reason the
    /// plugin registry does it — callers are INSTANCES and CI jobs, not signed-in users — with the
    /// bearer gate below doing the real work.
    /// </summary>
    public static IEndpointRouteBuilder MapContainerRegistry(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RoutePrefix).AllowAnonymous();

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var client = http.RequestServices.GetRequiredService<UpstreamRegistryClient>();
            var logger = http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(ContainerRegistryEndpoints));

            // Unconfigured is 404 on everything, not 401 and not a partial service. A mirror
            // without a credential cannot serve a single byte, and saying "unauthorised" would
            // send an operator hunting for a token problem that does not exist.
            if (!client.IsConfigured)
            {
                logger?.LogDebug(
                    "Container registry mirror: {Path} refused — {Section}:Upstream/Username/Password "
                    + "are not all set, so the mirror is off.",
                    http.Request.Path, ContainerRegistryOptions.SectionName);
                return Results.NotFound();
            }

            var authenticator = http.RequestServices
                .GetRequiredService<IContainerRegistryAuthenticator>();
            // 🚨 ObserveCompletion, never .ToTask() — a Task completed inside an Rx pipeline
            // resumes its awaiter INLINE on the signalling thread, still inside Rx's trampoline,
            // and everything the continuation then does inherits that scheduler.
            var caller = await authenticator
                .Authenticate(http.Request.Headers.Authorization.ToString(), http.RequestAborted)
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger?.LogWarning(ex,
                        "Container registry mirror: authentication for {Path} faulted after the "
                        + "request had already been answered", http.Request.Path),
                    http.RequestAborted);

            if (caller is null)
                return Challenge(http);

            http.Items[CallerItemKey] = caller;
            return await next(ctx);
        });

        // The spec's version probe. A client hits this first and reads the challenge from it, so
        // it must answer 200 (authenticated) or 401-with-challenge — never 404.
        group.MapGet("/", () => Results.Ok(new { }));

        group.MapGet("/{**rest}", (HttpContext http, CancellationToken ct) => Serve(http, ct));
        return endpoints;
    }

    /// <summary>
    /// The bearer challenge every OCI client expects before it will present a credential. Naming
    /// the realm is what makes `docker pull` fetch a token rather than simply failing.
    /// </summary>
    private static IResult Challenge(HttpContext http)
    {
        var realm = $"{http.Request.Scheme}://{http.Request.Host}{RoutePrefix}/token";
        http.Response.Headers["WWW-Authenticate"] =
            $"Bearer realm=\"{realm}\",service=\"{http.Request.Host}\"";
        return Results.Json(
            new { errors = new[] { new { code = "UNAUTHORIZED", message = "instance key required" } } },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> Serve(HttpContext http, CancellationToken ct)
    {
        var client = http.RequestServices.GetRequiredService<UpstreamRegistryClient>();
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ContainerRegistryEndpoints));

        var rest = (string?)http.Request.RouteValues["rest"] ?? string.Empty;
        if (!RegistryRoute.TryParse(rest, out var route))
            return Results.NotFound();

        // 🚨 The allowlist is the difference between a mirror and an open read proxy for the whole
        // upstream. Empty means NONE.
        var options = http.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ContainerRegistryOptions>>()
            .Value;
        if (!options.Repositories.Contains(route.Repository, StringComparer.Ordinal))
        {
            logger?.LogDebug(
                "Container registry mirror: repository {Repository} is not in {Section}:Repositories",
                route.Repository, ContainerRegistryOptions.SectionName);
            return Results.NotFound();
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.OpenAsync(
                HttpMethod.Get, route.Repository, "/v2/" + rest,
                http.Request.Headers.Range.ToString(), ct);
        }
        catch (UpstreamRegistryException ex)
        {
            // The mirror's own credential failed — that is a 502, never a 401. A 401 here would
            // tell the caller to fix ITS token, which is not the broken thing.
            logger?.LogWarning("Container registry mirror: upstream refused {Path} — {Reason}",
                http.Request.Path, ex.Message);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        return new UpstreamPassthroughResult(upstream);
    }
}
