using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The release gate as a READABLE service: <c>GET /api/plugins/is-updatable?version=…</c> answers
/// #1754's question — <i>may this environment be rolled to that release?</i> — for the instance
/// serving the request.
///
/// <para>It exists because the verdict has to be reachable by the paths that roll a version but do
/// not run inside the portal: CD's own post-promote assertion, and an operator about to
/// <c>kubectl set image</c>. Those paths must not re-derive the rule — the whole point of
/// <see cref="ReleaseAvailability"/> is that there is ONE rule — so they read the answer instead
/// of recomputing it. The portal's own poller calls the same
/// <see cref="ReleaseAvailabilityService"/> in-process.</para>
///
/// <para>🚨 <b>Auth is the instance key</b>, the same <c>mwi_</c> gate as the bundle routes, and it
/// fails CLOSED: the response names installed packages and the reasons a release is unsafe, which
/// is deployment inventory, not public information.</para>
///
/// <para><b>Scope, stated rather than implied:</b> an instance answers for ITSELF. The registry
/// records that an instance exists (<c>InstanceAutoRegistrationService</c>) but not what it has
/// installed, so no third party can answer for it today; each environment is asked at its own URL.
/// When #1735's per-environment composition lands, the declared package set becomes answerable
/// centrally and <see cref="ReleaseAvailabilityService"/> is the one place that changes — the rule
/// above does not.</para>
/// </summary>
public static class ReleaseGateEndpoints
{
    /// <summary>Route the gate is mounted at.</summary>
    public const string Route = "/api/plugins/is-updatable";

    /// <summary>Maps the instance-key-gated release gate. Call alongside <c>MapPluginBundles</c>.</summary>
    public static IEndpointRouteBuilder MapReleaseGate(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (HttpContext http, string? version, CancellationToken ct) =>
                Verdict(http, version, ct))
            .AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Verdict(HttpContext http, string? version, CancellationToken ct)
    {
        var authenticator = http.RequestServices
            .GetRequiredService<InstanceRegistryAuthenticator>();

        return authenticator.Authenticate(http.Request.Headers.Authorization)
            .SelectMany(caller => caller is null
                ? Observable.Return(Results.Json(
                    new { error = "A registered instance key is required (Authorization: Bearer mwi_… or Basic)." },
                    statusCode: StatusCodes.Status401Unauthorized))
                : Answer(http, version))
            .FirstAsync()
            .ToTask(ct);
    }

    private static IObservable<IResult> Answer(HttpContext http, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Observable.Return(Results.Json(
                new { error = "Query parameter 'version' is required — the release to be rolled to." },
                statusCode: StatusCodes.Status400BadRequest));

        var service = http.RequestServices.GetRequiredService<ReleaseAvailabilityService>();
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ReleaseGateEndpoints));

        return service.IsUpdatable(version)
            .Select(verdict =>
            {
                if (!verdict.IsUpdatable)
                    // Information, not Debug: a held environment must be findable in the logs
                    // without turning anything on. A silent hold is the outage this gate exists
                    // to prevent, not a quiet success.
                    logger?.LogInformation(
                        "Release gate: HOLD for {Version} — {Reason}", version, verdict.HoldReason);

                return Results.Json(new
                {
                    version,
                    isUpdatable = verdict.IsUpdatable,
                    // Distinguishes "the gate does not apply here" from "the gate passed" — the
                    // two must never render as the same tick.
                    enforced = verdict.NotEnforcedReason is null,
                    notEnforcedReason = verdict.NotEnforcedReason,
                    // 🚨 Distinguishes an availability failure from a compatibility verdict.
                    indeterminate = verdict.IsIndeterminate,
                    holdReason = verdict.HoldReason,
                    packages = verdict.Packages
                        .Select(p => new { package = p.Package, status = p.Kind.ToString(), reason = p.Reason })
                        .ToArray(),
                });
            });
    }
}
