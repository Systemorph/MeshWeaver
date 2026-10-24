using System.Reactive.Linq;
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
/// <c>GET /api/instances/seal-key</c> — a registered installation fetches the PUBLIC key it seals a secret to
/// before handing it to this control instance (<see cref="SecretEnvelope"/>). Authenticated by the installation's
/// instance key or its short-lived token, exactly like the token exchange.
///
/// <para>🚨 Authentication comes BEFORE anything is read or minted: the key pair is created on first use, and an
/// anonymous request must not be able to provoke that write. Only the public half is ever returned; the private
/// half stays <c>enc:</c>-protected in the Admin partition and never leaves <see cref="SecretSealingKeyService"/>.</para>
/// </summary>
public static class SealKeyEndpoints
{
    /// <summary>Maps the endpoint. Call alongside <c>MapInstanceTokenExchange</c>.</summary>
    public static IEndpointRouteBuilder MapSealKey(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SealKeyPayloads.Route, Get).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Get(HttpContext http, IMessageHub rootHub, CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(SealKeyEndpoints));
        var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();
        var keys = rootHub.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
        return authenticator.AuthenticateOutcome(http.Request.Headers.Authorization.ToString())
            .SelectMany(outcome =>
            {
                if (outcome.IsUnavailable)
                    return Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger));
                if (outcome.Instance is null)
                    return Observable.Return(Results.Json(
                        new { error = "A registered instance key is required (Authorization: Bearer mwi_…)." },
                        statusCode: StatusCodes.Status401Unauthorized));
                return keys.PublicKey().Select(key => Results.Json(
                    new SealKeyPayloads.Response(key.KeyId, key.PublicKey, SealKeyPayloads.Algorithm, SecretEnvelope.CurrentVersion),
                    SealKeyPayloads.Json));
            })
            .ObserveCompletion(
                ex => logger?.LogWarning(ex, "Seal-key request faulted after answering"), ct)
            .ContinueWith(t => t.Result ?? Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                TaskContinuationOptions.ExecuteSynchronously);
    }
}
