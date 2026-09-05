using System.IO;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The generic webhook inbox endpoint: <c>POST /api/hooks/{**target}</c> stores the raw delivery
/// as a <c>WebhookEvent</c> node at <c>{target}/_Inbox/{id}</c> (see
/// <see cref="WebhookInbox"/>). Anonymous by design — external services (Stripe, GitHub, …)
/// cannot authenticate — and fail-closed: only targets allowlisted under
/// <c>WebhookInbox:Targets</c> in configuration accept deliveries; everything else is 404.
///
/// <para>The endpoint speaks exactly ONE signature scheme, and only for targets that ask for it:
/// a target declaring <c>Targets:N:SecretConfigKey</c> has its <c>X-Hub-Signature-256</c> verified
/// over the raw body before anything is stored, and gets 401 when it does not verify (#3312 — a
/// 2xx that meant "I received bytes" made a MISMATCHED secret look exactly like a correct one, so
/// a publisher could not fail on it). Everything else keeps the dumb contract: no integration-
/// specific (e.g. payment) code lives in the portal, and Stripe-shaped schemes stay the consuming
/// plugin's job over the verbatim stored body + headers.</para>
/// </summary>
public static class WebhookInboxEndpoints
{
    /// <summary>Maps the anonymous <c>/api/hooks/{**target}</c> inbox endpoint. Call alongside
    /// <c>MapMeshApi</c>.</summary>
    public static IEndpointRouteBuilder MapWebhookInbox(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/hooks/{**target}",
                (string target, HttpRequest request, IMessageHub rootHub, IConfiguration config,
                        CancellationToken ct) =>
                    Deliver(target, request, rootHub, config, ct))
            .AllowAnonymous();
        return endpoints;
    }

    // The sanctioned Task boundary (a minimal-API handler, like the MCP/registry adapters):
    // the body is reactive — read, deliver, map to a status code.
    private static async Task<IResult> Deliver(
        string target, HttpRequest request, IMessageHub hub, IConfiguration config,
        CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(WebhookInboxEndpoints));
        var allowed = WebhookInbox.ReadTargets(config);

        // Refuse oversized bodies BEFORE buffering them (Content-Length first; the capped reader
        // below still guards chunked bodies that lie about their size).
        if (request.ContentLength is > WebhookInbox.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        // 🚨 The cap the comment above always promised. Content-Length is advisory (absent on a
        // chunked request, and a client may lie); the reader enforces the byte limit itself.
        var body = await BoundedBody.ReadAsync(request.Body, WebhookInbox.MaxBodyBytes, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var headers = request.Headers.Select(h =>
            new KeyValuePair<string, string>(h.Key, h.Value.ToString()));

        var result = (await WebhookInbox.Deliver(
                hub, allowed, target, request.ContentType, headers, body)
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Webhook delivery for target '{Target}' faulted after the response had already been sent",
                    target),
                ct))!;
        switch (result.Status)
        {
            case WebhookInbox.DeliveryStatus.Accepted:
                logger?.LogInformation("Webhook stored at {Path}", result.NodePath);
                // 🚨 The body, not the status, is what a signing sender must read. Both branches
                // are 200: "verified" means this instance checked the HMAC, "not-required" means
                // the target declares no SecretConfigKey here and the signature was never looked
                // at. Answering a bare 200 to both is how a chart value going missing would take
                // verification away again without a single red anything (#3312).
                return Results.Json(new
                {
                    status = "accepted",
                    signature = result.SignatureVerified ? "verified" : "not-required",
                });
            case WebhookInbox.DeliveryStatus.TooLarge:
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            case WebhookInbox.DeliveryStatus.SignatureInvalid:
                // Warning, not Information: on a target that declares a secret this is either an
                // attacker or — far more often — the two halves of a shared secret having drifted,
                // which is invisible from the sending side except through this status.
                logger?.LogWarning(
                    "Webhook for target '{Target}' REFUSED: {Header} absent or not verifying "
                    + "against the secret named by {Key}. Nothing was stored.",
                    target, WebhookInbox.SignatureHeader, WebhookInbox.SecretConfigKeyName);
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            case WebhookInbox.DeliveryStatus.SecretUnavailable:
                // OUR misconfiguration, not the caller's — hence 500, and Error: this target
                // refuses every delivery until the declared key resolves to something.
                logger?.LogError(
                    "Webhook for target '{Target}' REFUSED: it declares {Key} but that "
                    + "configuration key is empty on this instance, so no delivery to it can be "
                    + "verified. Nothing was stored.",
                    target, WebhookInbox.SecretConfigKeyName);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            default:
                // Unknown target: no detail leaks about which paths exist or are allowlisted.
                logger?.LogWarning("Webhook for unknown/refused target '{Target}' dropped", target);
                return Results.NotFound();
        }
    }
}
