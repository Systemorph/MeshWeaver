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
/// <c>GET /api/instances/seal-key</c> — the PUBLIC key an installation seals a secret to before handing it to this
/// control instance (<see cref="SecretEnvelope"/>). Anonymous on purpose: it is a public key, and an installation
/// that holds only its announcement key (the control-inbox credential) must be able to fetch it.
///
/// <para>🚨 <b>It is read-only.</b> The key pair is minted by the control plane itself (<see cref="SecretSealingKeyService.PublicKey"/>),
/// never by a request, so an anonymous caller cannot provoke a write. Until the pair exists the answer is 404.
/// Only the public half is ever returned; the private half stays <c>enc:</c>-protected in the Admin partition and
/// never leaves <see cref="SecretSealingKeyService"/>. What the key is FOR is gated elsewhere: a value sealed to it is
/// accepted only inside an event signed with the deployment's own key.</para>
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
        var keys = rootHub.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
        return keys.Existing()
            .Select(key => key is null
                ? Results.Json(new { error = "no sealing key has been minted on this instance" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(
                    new SealKeyPayloads.Response(key.KeyId, key.PublicKey, SealKeyPayloads.Algorithm, SecretEnvelope.CurrentVersion),
                    SealKeyPayloads.Json))
            .ObserveCompletion(ex => logger?.LogWarning(ex, "Seal-key request faulted after answering"), ct)
            .ContinueWith(t => t.Result ?? Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                TaskContinuationOptions.ExecuteSynchronously);
    }
}
