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

    /// <summary>
    /// The body of a 200 answer — the contract a signing sender reads (#3312):
    /// <c>status</c> is <c>accepted</c> (stored) or <c>verified</c> (a verify-only test, nothing
    /// stored); <c>signature</c> is <c>verified</c> or <c>not-required</c>; <c>sender</c> names the
    /// per-sender key that verified (a name, never a value) or is null for the shared secret.
    /// Pure, and public so a test can answer exactly as the endpoint does.
    /// </summary>
    public static WebhookAnswer AcceptedAnswer(WebhookInbox.DeliveryResult result) => new(
        result.VerifyOnly ? "verified" : "accepted",
        result.SignatureVerified ? "verified" : "not-required",
        result.SenderKey);

    /// <summary>The JSON body of a 200 answer (see <see cref="AcceptedAnswer"/>).</summary>
    /// <param name="Status"><c>accepted</c> or <c>verified</c>.</param>
    /// <param name="Signature"><c>verified</c> or <c>not-required</c>.</param>
    /// <param name="Sender">The per-sender key's name, or null.</param>
    public sealed record WebhookAnswer(
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("signature")] string Signature,
        [property: System.Text.Json.Serialization.JsonPropertyName("sender")] string? Sender);

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
        // 🚨 Same two outcomes, kept apart, as the GitHub endpoint (#4860): a client that drops
        // mid-body raises Kestrel's "Unexpected end of request content" and is a 400, while `null`
        // means over the cap and is a 413. Folding them would answer the wrong status AND report a
        // cap breach that never happened. This is the OTHER BoundedBody call site — the fix is
        // worth nothing if only the one the incident happened to name is corrected.
        string? body;
        try
        {
            body = await BoundedBody.ReadAsync(request.Body, WebhookInbox.MaxBodyBytes, ct);
        }
        catch (BadHttpRequestException ex)
        {
            // 🚨 The exception's own status, not a fixed 400 — Kestrel reuses this type with 413 for
            // its MaxRequestBodySize breach, and this endpoint answers 413 for its OWN cap both
            // above and below. See the same catch in GitHubWebhookEndpoints.
            logger?.LogDebug(ex,
                "Webhook body read for target '{Target}' failed with {Status}: {Reason}.",
                target, ex.StatusCode, ex.Message);
            return Results.StatusCode(ex.StatusCode);
        }

        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var headers = request.Headers.Select(h =>
            new KeyValuePair<string, string>(h.Key, h.Value.ToString()));

        // 🚨 A STALLED read is 503 + Retry-After, never an unhandled 500 (#6125). Before storing,
        // Deliver confirms the target node exists with a `path:{target}` query, and when a query
        // provider never delivers its Initial the fan-in terminates that read with
        // QueryProviderStalledException (policy query-fanin-stall-terminal) — an availability
        // failure that says, in its own text, that it is retryable. It used to escape this handler,
        // so ASP.NET's exception middleware answered a bare 500 and logged the stall as a defect of
        // the ENDPOINT. Same mapping as the plugin-bundle routes (#5345): only what
        // AreaErrorClassifier.IsStorageUnavailable classifies becomes 503; every other fault still
        // escapes, so a real defect keeps surfacing as one. Nothing was stored on that path, so a
        // sender that delivers again loses nothing and duplicates nothing.
        return (await WebhookInbox.Deliver(
                hub, allowed, target, request.ContentType, headers, body)
            .Select(result => Answer(result, target, logger))
            .UnavailableOnAStalledRead(request.HttpContext, logger, StalledReadError)
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex,
                    "Webhook delivery for target '{Target}' faulted after the response had already been sent",
                    target),
                ct))!;
    }

    /// <summary>The 503 body's <c>error</c> when the delivery's own read could not be answered.
    /// Nothing was stored, and the text says so: the sender's next move is to deliver again.</summary>
    internal const string StalledReadError =
        "The inbox could not confirm its target just now — nothing was stored; retry shortly.";

    /// <summary>Maps a delivery's verdict to its HTTP answer, with the log line that goes with it.</summary>
    private static IResult Answer(WebhookInbox.DeliveryResult result, string target, ILogger? logger)
    {
        switch (result.Status)
        {
            case WebhookInbox.DeliveryStatus.Accepted when result.VerifyOnly:
                // A sender TESTING its key (WebhookInbox.VerifyOnlyHeader): verified, nothing stored.
                // `sender` names which per-sender key verified (a name, never a value), so the sender
                // can show its operator "accepted as 'fabrikam'" rather than a bare yes.
                logger?.LogInformation(
                    "Webhook verify-only test for target '{Target}' verified{Sender} — nothing stored",
                    target, result.SenderKey is { } testedAs ? $" with the per-sender key '{testedAs}'" : "");
                return Results.Json(AcceptedAnswer(result));
            case WebhookInbox.DeliveryStatus.Accepted:
                // The sender key is a NAME (e.g. a deployment id), never a value: it says which
                // per-sender key verified, so the consumer's attribution can be read from the log.
                if (result.SenderKey is { } senderKey)
                    logger?.LogInformation(
                        "Webhook stored at {Path} — verified with the per-sender key '{SenderKey}'",
                        result.NodePath, senderKey);
                else
                    logger?.LogInformation("Webhook stored at {Path}", result.NodePath);
                // 🚨 The body, not the status, is what a signing sender must read. Both branches
                // are 200: "verified" means this instance checked the HMAC, "not-required" means
                // the target declares no SecretConfigKey here and the signature was never looked
                // at. Answering a bare 200 to both is how a chart value going missing would take
                // verification away again without a single red anything (#3312).
                return Results.Json(AcceptedAnswer(result));
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
