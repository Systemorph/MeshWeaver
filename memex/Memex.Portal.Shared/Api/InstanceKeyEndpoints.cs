using System.Reactive.Linq;
using System.Text.Json;
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
/// The wire shape of the registry's key-lifecycle surface (<see cref="InstanceKeyEndpoints"/>) — the
/// routes and the two JSON bodies. The consumer is the hosting operator's shell
/// (<c>hosting-kv-rotate</c>, <c>hosting-registry-key</c>), which reads <c>instanceId</c> and
/// <c>key</c> with <c>jq</c>; renaming a field here breaks it silently, so the names are pinned by
/// <c>InstanceKeyRotationTest</c>.
/// </summary>
public static class InstanceKeyPayloads
{
    /// <summary><c>GET</c> — which instance the presented key belongs to, and which of its slots.</summary>
    public const string SelfRoute = "/api/instances/self";

    /// <summary><c>POST</c> <see cref="StageRequest"/> — stage the next key's hash.</summary>
    public const string StageRoute = "/api/instances/self/key/stage";

    /// <summary><c>POST</c> — commit the staged key; must present the staged key.</summary>
    public const string CommitRoute = "/api/instances/self/key/commit";

    /// <summary><c>POST</c> — revoke the presented key without a successor.</summary>
    public const string RevokeRoute = "/api/instances/self/key/revoke";

    /// <summary>The slot word for the current key.</summary>
    public const string CurrentKey = "current";

    /// <summary>The slot word for a staged key.</summary>
    public const string StagedKey = "staged";

    /// <summary>The body of a stage request: the lowercase SHA-256 hex of the new raw key — never the key.</summary>
    /// <param name="KeyHash">64 lowercase hex characters.</param>
    public sealed record StageRequest(string KeyHash);

    /// <summary>The answer of every key endpoint.</summary>
    /// <param name="InstanceId">The instance the presented key belongs to.</param>
    /// <param name="Key">The slot the PRESENTED key occupies after the call (<see cref="CurrentKey"/>,
    /// <see cref="StagedKey"/>), or <c>revoked</c>.</param>
    /// <param name="Staged">Whether a staged key is outstanding after the call.</param>
    public sealed record State(string InstanceId, string Key, bool Staged);

    /// <summary>The serializer the endpoints answer with — camelCase, so the shell reads <c>.instanceId</c>.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The registry's key-lifecycle surface — how an instance's plugin-registry key is ROTATED and
/// REVOKED at the registry itself (MeshWeaver#2802).
///
/// <list type="table">
/// <item><term><c>GET /api/instances/self</c></term><description>which instance the presented key is,
/// and whether it is its current or its staged key</description></item>
/// <item><term><c>POST /api/instances/self/key/stage</c></term><description>stage the next key's HASH;
/// both keys then authenticate</description></item>
/// <item><term><c>POST /api/instances/self/key/commit</c></term><description>presenting the STAGED key,
/// promote it and retire the old one</description></item>
/// <item><term><c>POST /api/instances/self/key/revoke</c></term><description>stop the presented key
/// authenticating, with no successor</description></item>
/// </list>
///
/// <para>🚨 <b>Why it lives HERE, on the registry, and not in the control plane.</b> The first rotation
/// design adopted the new hash through whatever <see cref="IInstanceKeyRegistry"/> the control
/// instance's hub happened to resolve — and every Memex portal registers one, while the instances
/// live only in the registry's store. On the control instance the adoption therefore ran against a
/// store that does not hold the instance, AFTER the operator had already written the new key to Key
/// Vault. Serving the lifecycle from the registry makes the question "which store?" unaskable: the
/// store that answers is the one that authenticated the key.</para>
///
/// <para>🚨 <b>Authorised by POSSESSION, never by a standing credential.</b> Every call presents a key of
/// the instance (<c>Authorization: Bearer mwi_…</c>) and may act only on THAT instance, in the slot
/// that key occupies (<see cref="InstanceKeyRotation"/>). There is deliberately no admin token that
/// can re-key someone else's instance: the keys this protocol replaces sat in plaintext pod specs
/// (MeshWeaver#3201), and a credential that could re-key the whole fleet is the worst thing such a
/// leak could have carried. A short-lived <c>mwa_</c> token is refused here — it may read the
/// catalog, never re-key or revoke. Only hashes cross in the other direction.</para>
/// </summary>
public static class InstanceKeyEndpoints
{
    /// <summary>Maps the key-lifecycle routes. Called by <c>MapInstanceRegistration</c>, so every
    /// host that registers instances also lets them rotate.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapInstanceKeyRotation(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(InstanceKeyPayloads.SelfRoute, Self).AllowAnonymous();
        endpoints.MapPost(InstanceKeyPayloads.StageRoute, Stage).AllowAnonymous();
        endpoints.MapPost(InstanceKeyPayloads.CommitRoute, Commit).AllowAnonymous();
        endpoints.MapPost(InstanceKeyPayloads.RevokeRoute, Revoke).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Self(HttpContext http, IMessageHub rootHub, CancellationToken ct) =>
        WithCaller(http, rootHub, ct, (caller, presented, _) =>
            Observable.Return(Answer(caller.Instance, presented)));

    private static Task<IResult> Stage(
        HttpContext http, IMessageHub rootHub, InstanceKeyPayloads.StageRequest? body, CancellationToken ct) =>
        WithCaller(http, rootHub, ct, (caller, presented, instances) =>
            InstanceKeyRotation.IsKeyHash(body?.KeyHash)
                ? instances.StageKeyHash(caller.InstancePath!, presented, body!.KeyHash)
                    .Select(next => Answer(next, presented))
                : Observable.Return(Results.Json(
                    new { error = "keyHash must be the lowercase SHA-256 hex of the new raw key (64 hex chars). "
                                  + "Send the HASH — the registry never receives a key." },
                    statusCode: StatusCodes.Status400BadRequest)));

    private static Task<IResult> Commit(HttpContext http, IMessageHub rootHub, CancellationToken ct) =>
        WithCaller(http, rootHub, ct, (caller, presented, instances) =>
            instances.CommitStagedKey(caller.InstancePath!, presented)
                .Select(next => Answer(next, presented)));

    private static Task<IResult> Revoke(HttpContext http, IMessageHub rootHub, CancellationToken ct) =>
        WithCaller(http, rootHub, ct, (caller, presented, instances) =>
            instances.RevokePresentedKey(caller.InstancePath!, presented)
                .Select(next => Results.Json(
                    new InstanceKeyPayloads.State(next.InstanceId, "revoked", !string.IsNullOrEmpty(next.PendingKeyHash)),
                    InstanceKeyPayloads.Json)));

    private static IResult Answer(MeshWeaverInstance instance, string presentedHash) =>
        Results.Json(
            new InstanceKeyPayloads.State(
                instance.InstanceId,
                InstanceKeyRotation.SlotOf(instance, presentedHash) == InstanceKeySlot.Staged
                    ? InstanceKeyPayloads.StagedKey
                    : InstanceKeyPayloads.CurrentKey,
                !string.IsNullOrEmpty(instance.PendingKeyHash)),
            InstanceKeyPayloads.Json);

    /// <summary>
    /// The one gate every key route passes: a durable instance key in the header (a token is
    /// refused), resolved by the registry's own authenticator (unavailable → 503, unknown → 401),
    /// then the route's work, with a refused transition answered 409 and nothing else leaking a
    /// stack. The key's HASH is what the route works with; the key itself never leaves this method.
    /// </summary>
    private static Task<IResult> WithCaller(
        HttpContext http, IMessageHub rootHub, CancellationToken ct,
        Func<AuthenticatedInstance, string, MeshWeaverInstanceService, IObservable<IResult>> work)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceKeyEndpoints));
        var header = http.Request.Headers.Authorization.ToString();
        var rawKey = InstanceKeys.ExtractKey(header);
        if (rawKey is null)
            return Task.FromResult(Results.Json(
                new { error = "The instance's own key is required (Authorization: Bearer mwi_…). A short-lived "
                              + "token may read the catalog; it can never re-key or revoke." },
                statusCode: StatusCodes.Status401Unauthorized));
        var presented = InstanceKeys.Hash(rawKey);

        var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();
        // Request services first, then the mesh's — the same resolution MapInstanceRegistration uses.
        var instances = http.RequestServices.GetService<MeshWeaverInstanceService>()
            ?? rootHub.ServiceProvider.GetRequiredService<MeshWeaverInstanceService>();

        return authenticator.AuthenticateOutcome(header)
            .SelectMany(outcome =>
            {
                if (outcome.IsUnavailable)
                    return Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger));
                if (outcome.Instance is not { InstancePath: { Length: > 0 } } caller)
                {
                    // 🚨 THE answer a portal that is not the registry gives — and the one a rotation
                    // must meet BEFORE anything is minted: this store does not know the key, so it
                    // holds no instance to rotate. Not "maybe later"; a definitive 401.
                    logger?.LogWarning(
                        "Key-lifecycle call {Path} presented an instance key this registry does not accept "
                        + "(hash prefix {Prefix})", http.Request.Path, InstanceKeys.HashPrefix(presented));
                    return Observable.Return(Results.Json(
                        new { error = "This registry does not accept the presented instance key — it holds no "
                                      + "instance that key belongs to. Nothing was changed." },
                        statusCode: StatusCodes.Status401Unauthorized));
                }
                return work(caller, presented, instances);
            })
            .Catch((InstanceKeyRefusedException ex) =>
            {
                logger?.LogWarning("Key-lifecycle call {Path} refused for {InstanceId}: {Reason}",
                    http.Request.Path, ex.InstanceId, ex.Reason);
                return Observable.Return(Results.Json(
                    new { error = ex.Reason }, statusCode: StatusCodes.Status409Conflict));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Key-lifecycle call {Path} failed", http.Request.Path);
                return Observable.Return(Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Key-lifecycle call faulted after the response had already been sent"),
                ct)!;
    }
}
