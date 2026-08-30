using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
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
/// <c>POST /api/instances/register</c> — first-startup instance auto-registration. A NEW deployment
/// presents a registration bootstrap key (<c>mwr_…</c>, minted by a platform admin) and its desired
/// instance id; the registry creates the <see cref="MeshWeaverInstance"/> — owned by the admin who
/// minted the bootstrap key, exactly as if they had registered it by hand — and returns the
/// instance's own <c>mwi_</c> key ONCE. <c>PluginCatalog:DefaultGrants</c> seeding applies, so with
/// the platform default configured the new instance can pull <c>Plugins/*</c> immediately.
///
/// <para>🚨 The endpoint is anonymous at the ASP.NET layer — the bootstrap key in the body IS the
/// authentication (<see cref="RegistrationKeyService.Resolve"/>: hash → index → record, revocation
/// and expiry enforced). An invalid key is 401; a taken id is 409 and never reveals whose; a
/// malformed id is 400. The consumer half is <see cref="InstanceAutoRegistrationService"/>; the
/// wire shape is <see cref="InstanceRegistrationPayloads"/> so the two cannot drift.</para>
/// </summary>
public static class InstanceRegistrationEndpoints
{
    /// <summary>Maps the registration endpoint. Call alongside <c>MapPluginRegistry</c>.</summary>
    public static IEndpointRouteBuilder MapInstanceRegistration(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(InstanceRegistrationPayloads.Route, Register).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Register(
        HttpContext http, IMessageHub rootHub, InstanceRegistrationPayloads.Request body, CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(InstanceRegistrationEndpoints));
        // Request services first, then the mesh's — the same two-step resolution the bundle routes
        // use for their anchor, so a host (or a test) can wire an instance service with its own
        // configuration without a second resolution rule.
        var instances = http.RequestServices.GetService<MeshWeaverInstanceService>()
            ?? rootHub.ServiceProvider.GetRequiredService<MeshWeaverInstanceService>();

        if (!MeshWeaverInstanceService.IsValidInstanceId(body.InstanceId))
            return Task.FromResult(Results.Json(
                new { error = "instanceId must be 3–48 chars: lowercase letters, digits and single "
                              + "hyphens, not starting or ending with a hyphen." },
                statusCode: StatusCodes.Status400BadRequest));

        // No key at all is OPEN registration — accepted only where the registry's operator
        // configured a key for un-keyed callers (the free plan on memex.meshweaver.cloud), refused
        // with the same 401 as an invalid key everywhere else. The caller never learns which.
        var open = string.IsNullOrWhiteSpace(body.BootstrapKey);
        return (open
                ? instances.RegisterOpen(body.InstanceId, body.DisplayName, body.Description, body.HomeUrl)
                : instances.RegisterWithBootstrapKey(
                    body.BootstrapKey, body.InstanceId, body.DisplayName, body.Description, body.HomeUrl))
            .Select(registration => (IResult)Results.Json(
                new InstanceRegistrationPayloads.Response(
                    registration.Instance.InstanceId, registration.RawKey),
                InstanceRegistrationPayloads.Json))
            .Catch((InvalidBootstrapKeyException ex) =>
            {
                if (open)
                    logger?.LogWarning(
                        "OPEN instance registration rejected for id '{InstanceId}' — this registry "
                        + "configures no {Key}", body.InstanceId,
                        MeshWeaverInstanceService.OpenRegistrationKeyConfigKey);
                else
                    logger?.LogWarning(
                        "Instance registration rejected for id '{InstanceId}' — invalid, revoked or "
                        + "expired bootstrap key", body.InstanceId);
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized));
            })
            .Catch((InstanceIdTakenException ex) => Observable.Return((IResult)Results.Json(
                new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict)))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Instance registration for '{InstanceId}' failed", body.InstanceId);
                return Observable.Return((IResult)Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Instance registration for '{InstanceId}' faulted after the response had already been sent",
                    body.InstanceId),
                ct)!;
    }
}
