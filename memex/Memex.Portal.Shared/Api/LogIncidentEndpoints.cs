using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// Ingest surface for the in-cluster log watcher: <c>POST /api/log-incidents</c> takes one
/// already-fingerprinted red-log burst and folds it into the mesh (see
/// <see cref="LogIncidentIngestService"/>). This is the ONLY way a burst enters the portal — the
/// detector runs outside, so that noticing "the portal is throwing errors" never depends on the
/// portal being healthy.
///
/// <para>🚨 <b>Token-gated, and absent when unconfigured.</b> The caller presents the shared secret
/// from <c>LogWatch:IngestToken</c> as <c>Authorization: Bearer …</c>. With no token configured the
/// route is <b>not mapped at all</b> rather than mapped-and-open: reaching this endpoint spends
/// model budget (every new fingerprint starts an agent round) and opens GitHub issues, so an open
/// version of it is an abuse vector, not a convenience. That is the same fail-closed lesson as the
/// plugin registry's anonymous mode (2026-08-06).</para>
/// </summary>
public static class LogIncidentEndpoints
{
    /// <summary>The route the watcher POSTs to.</summary>
    public const string Route = "/api/log-incidents";

    /// <summary>
    /// Maps the token-gated ingest endpoint — a no-op when <c>LogWatch:IngestToken</c> is unset.
    /// Call alongside <c>MapMeshApi</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapLogIncidents(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<IOptions<LogWatchOptions>>()?.Value;
        var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(LogIncidentEndpoints));

        if (options?.IngestToken is not { Length: > 0 } expected)
        {
            logger?.LogInformation(
                "Red-log ticketing is off: {Route} is not mapped because LogWatch:IngestToken is unset.",
                Route);
            return endpoints;
        }

        // AllowAnonymous at the ASP.NET auth layer: the caller is a cluster service, not a
        // signed-in user, so the user auth schemes do not apply. The shared secret below IS the gate.
        endpoints.MapPost(Route, (
                    HttpContext http,
                    LogIncidentReport report,
                    LogIncidentIngestService ingest,
                    CancellationToken ct) =>
                Ingest(http, report, ingest, expected, logger, ct))
            .AllowAnonymous();

        logger?.LogInformation("Red-log ingest mapped at {Route}.", Route);
        return endpoints;
    }

    // The sanctioned Task boundary (a minimal-API handler, like the MCP/registry adapters):
    // the body is reactive — authorize, ingest, map to a status code.
    private static Task<IResult> Ingest(
        HttpContext http,
        LogIncidentReport report,
        LogIncidentIngestService ingest,
        string expectedToken,
        ILogger? logger,
        CancellationToken ct)
    {
        if (!IsAuthorized(http, expectedToken))
        {
            logger?.LogWarning("Red-log ingest: rejected a report — no valid ingest token presented.");
            return Task.FromResult(Results.Json(
                new { error = "A valid ingest token is required (Authorization: Bearer …)." },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        if (string.IsNullOrWhiteSpace(report.Fingerprint) || string.IsNullOrWhiteSpace(report.Category))
            return Task.FromResult(Results.Json(
                new { error = "Fields 'fingerprint' and 'category' are required." },
                statusCode: StatusCodes.Status400BadRequest));

        return ingest.Report(report)
            .Select(result => (IResult)Results.Ok(result))
            .Catch((Exception ex) =>
            {
                // Surface the failure (502) rather than pretend the burst was recorded: the watcher
                // retries on a non-2xx, so a swallowed error here would silently drop the incident.
                logger?.LogWarning(ex, "Red-log ingest failed for {Fingerprint}", report.Fingerprint);
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync().ToTask(ct);
    }

    /// <summary>
    /// Constant-time comparison of the presented bearer token against the configured one. Fixed-time
    /// so the endpoint cannot be turned into an oracle that leaks the token one byte at a time.
    /// </summary>
    private static bool IsAuthorized(HttpContext http, string expected)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var presented = header[scheme.Length..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}
