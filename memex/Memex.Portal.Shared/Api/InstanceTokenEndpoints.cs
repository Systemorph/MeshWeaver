using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// <c>POST /api/instances/token</c> — a registered instance exchanges its durable <c>mwi_</c> key
/// for a short-lived, scoped <c>mwa_</c> access token.
///
/// <para><b>Why it exists.</b> Without it a consumer must present its long-lived credential on every
/// call, which is the property that makes a standing per-repo PAT unacceptable. With it, a build
/// agent mints a minutes-long token narrowed to the one package it needs, and holds nothing durable
/// beyond the run.</para>
///
/// <para>🚨 <b>A token cannot be exchanged for another token.</b> The endpoint accepts ONLY the
/// durable instance key: allowing a token to mint its successor would turn a minutes-long credential
/// into a perpetual one by renewal, defeating the entire point of the expiry. The check is on the
/// credential's SHAPE (<c>mwi_</c> vs <c>mwa_</c>, disjoint by construction), not on a claim the
/// presenter could edit.</para>
///
/// <para>The token narrows; it never widens. The effective scope is what the caller asked for
/// intersected with what its sync licence already allows, and the live grant is re-read on every
/// subsequent request — so revoking a licence takes effect at once rather than when the token
/// expires.</para>
/// </summary>
public static class InstanceTokenEndpoints
{
    /// <summary>Maps the token-exchange endpoint. Call alongside <c>MapPluginRegistry</c>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapInstanceTokenExchange(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(SyncTokenPayloads.Route, Exchange).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Exchange(
        HttpContext http, IMessageHub rootHub, SyncTokenPayloads.Request? body, CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(InstanceTokenEndpoints));

        var header = http.Request.Headers.Authorization.ToString();

        // Shape check FIRST — a token may never mint its successor (see the type remarks).
        if (InstanceKeys.ExtractKey(header) is null)
        {
            var wasToken = SyncAccessToken.ExtractToken(header) is not null;
            if (wasToken)
                logger?.LogWarning(
                    "A sync access token was presented to the exchange endpoint — only the durable "
                    + "instance key may mint a token.");
            return Task.FromResult(Results.Json(
                new { error = wasToken
                    ? "A short-lived token cannot be exchanged for another token. Present the "
                      + "instance key (Authorization: Bearer mwi_…)."
                    : "A registered instance key is required (Authorization: Bearer mwi_…)." },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var options = rootHub.ServiceProvider.GetService<PluginCatalogOptions>();
        var signingKey = string.IsNullOrWhiteSpace(options?.TokenSigningKey)
            ? null
            : Encoding.UTF8.GetBytes(options!.TokenSigningKey);

        // Unconfigured is reported, never worked around with a weaker key — and it is a 503 rather
        // than a 401, because the caller's credential is fine and the registry's deployment is not.
        if (!SyncAccessToken.IsUsableSigningKey(signingKey))
        {
            logger?.LogError(
                "Token exchange requested but {Section}:{Key} is unset or shorter than {Min} bytes — "
                + "the endpoint cannot mint. Configure it identically on every replica.",
                PluginCatalogOptions.SectionName, nameof(PluginCatalogOptions.TokenSigningKey),
                SyncAccessToken.MinimumSigningKeyBytes);
            return Task.FromResult(Results.Json(
                new { error = "Token exchange is not configured on this registry." },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();
        var now = DateTimeOffset.UtcNow;

        return authenticator.Authenticate(header)
            .Select(caller =>
            {
                if (caller is null)
                    return Results.Json(
                        new { error = "A registered instance key is required (Authorization: Bearer mwi_…)." },
                        statusCode: StatusCodes.Status401Unauthorized);

                var effective = EffectiveScope(caller, body?.Scope, now);

                // Licensed for nothing (or for nothing that was asked for) means there is nothing to
                // mint a token FOR. Answering 403 with the reason beats handing back a token that
                // will 404 on every call.
                if (effective.Count == 0)
                    return Results.Json(
                        new { error = "This instance holds no current sync licence for the requested scope." },
                        statusCode: StatusCodes.Status403Forbidden);

                var lifetime = body?.LifetimeSeconds is > 0
                    ? TimeSpan.FromSeconds(body.LifetimeSeconds.Value)
                    : SyncAccessToken.DefaultLifetime;

                var token = SyncAccessToken.Mint(
                    caller.Instance.InstanceId, caller.Instance.KeyHash, effective, now, lifetime, signingKey!);
                var claims = SyncAccessToken.Verify(token, now, signingKey!)!;

                logger?.LogInformation(
                    "Minted a sync access token for {InstanceId}, scope [{Scope}], expiring {Expiry}",
                    caller.Instance.InstanceId, string.Join(", ", effective), claims.ExpiresAt);

                return Results.Json(
                    new SyncTokenPayloads.Response(
                        token, SyncAccessToken.Scheme,
                        (int)(claims.ExpiresAt - now).TotalSeconds, effective),
                    SyncTokenPayloads.Json);
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Sync access token exchange failed");
                return Observable.Return(Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync().ToTask(ct);
    }

    /// <summary>
    /// What the token will actually carry: the caller's CURRENT licence entries, narrowed to what
    /// was requested.
    ///
    /// <para>Expired and revoked entries are dropped here, so a token is never minted for a licence
    /// that has already ended — the grant would refuse it on use anyway, and handing out a token
    /// that cannot work is a worse answer than saying so.</para>
    /// </summary>
    private static IReadOnlyCollection<string> EffectiveScope(
        AuthenticatedInstance caller, IReadOnlyCollection<string>? requested, DateTimeOffset now)
    {
        if (caller.Grant.IsRevoked)
            return [];

        var licensed = caller.Grant.Entries
            .Where(e => e.IsValidAt(now))
            .ToList();

        if (requested is not { Count: > 0 })
            return [.. licensed.Select(e => e.ToString()).Distinct(StringComparer.Ordinal)];

        // Keep only requests the licence actually covers. Dropping rather than refusing: a token can
        // only narrow, so an over-broad request is a stale caller, not an attack.
        return
        [
            .. requested
                .Select(PluginGrantEntry.TryParse)
                .Where(e => e is not null)
                .Where(e => licensed.Any(l => l.Matches(e!.Source, e.PackageId)))
                .Select(e => e!.ToString())
                .Distinct(StringComparer.Ordinal)
        ];
    }
}
