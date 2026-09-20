using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.GitSync;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// The GitHub webhook receiver: <c>POST /webhooks/github</c>. GitHub calls this (unauthenticated
/// by browser session — it is authenticated by the HMAC signature), so it verifies
/// <c>X-Hub-Signature-256</c> against the shared secret <c>GitHub:Webhook:Secret</c> before doing
/// any work, then hands the event to <see cref="GitHubWebhookProcessor"/> which refreshes the
/// synced <c>{space}/_Issue/{number}</c> nodes of every Space that syncs the event's repository.
///
/// <para>Register ONE webhook per repo in GitHub → Settings → Webhooks, pointing at
/// <c>https://{host}/webhooks/github</c>, content-type <c>application/json</c>, with the same
/// secret, subscribed to the <c>Issues</c> + <c>Issue comments</c> + <c>Pushes</c> events —
/// a push triggers the headless "Update to latest" for every Space sync source matching the
/// pushed repo/branch/subdirectory, so GitSync'd Spaces stay current without polling. The
/// <c>async</c> here is the sanctioned HTTP-boundary bridge (mirrors
/// <see cref="GitHubConnectEndpoints"/>); the processing itself is reactive.</para>
/// </summary>
public static class GitHubWebhookEndpoints
{
    /// <summary>
    /// Hard ceiling on the request body this anonymous endpoint will buffer before the HMAC can be
    /// checked. 25 MiB is GitHub's own documented maximum payload, so it can never refuse a genuine
    /// delivery — it only bounds what a forged one costs.
    /// </summary>
    public const long MaxWebhookBodyBytes = 25L * 1024 * 1024;

    private const string WebhookPath = "/webhooks/github";

    public static IEndpointRouteBuilder MapGitHubWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(WebhookPath, async (
            HttpContext http,
            GitHubWebhookProcessor processor,
            IConfiguration config,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("GitHubWebhook");

            var secret = config["GitHub:Webhook:Secret"];
            if (string.IsNullOrEmpty(secret))
            {
                logger.LogWarning("GitHub webhook received but no secret is configured (GitHub:Webhook:Secret).");
                return Results.StatusCode(503);
            }

            // Read the raw body (needed byte-exact for the HMAC) — UNDER A CAP.
            //
            // 🚨 This endpoint is anonymous: the HMAC below is the ONLY thing authenticating it, and
            // it cannot run until the body is in memory. A plain CopyToAsync therefore let an
            // unauthenticated caller make the server buffer an arbitrarily large request before a
            // single byte was checked, and a chunked request carries no Content-Length to reject it
            // on (#2302). The cap bounds that to one GitHub-sized delivery per request.
            //
            // 25 MiB is GitHub's own documented maximum payload, so this can never refuse a genuine
            // delivery — it is strictly a ceiling on what a forged one can cost.
            //
            // 🚨 A DELIVERY THAT STOPS SHORT IS A 400, NOT AN UNHANDLED FAULT (#4860). GitHub's
            // delivery can time out, and an abandoned probe or a reset connection does the same:
            // Kestrel raises "Unexpected end of request content" from the body read. That escaped to
            // ExceptionHandlerMiddleware and was logged at `fail` with a stack trace — six times in
            // 19 days across five pods — for an ordinary network condition nobody can act on.
            // Caught as its own outcome, never folded into the `null` below: that one means OVER
            // THE CAP and answers 413, and a dropped connection is not that.
            byte[]? body;
            try
            {
                body = await BoundedBody.ReadBytesAsync(
                    http.Request.Body, MaxWebhookBodyBytes, http.RequestAborted);
            }
            catch (BadHttpRequestException ex)
            {
                // 🚨 ANSWER THE EXCEPTION'S OWN STATUS, never a fixed 400. Kestrel raises this same
                // type with 413 when its MaxRequestBodySize is exceeded — so hard-coding 400 here
                // would turn a server-limit breach into a bad-request, and the identical oversized
                // delivery would then report 413 or 400 depending only on which limit noticed it
                // first (this endpoint's own cap answers 413 a few lines below). That is precisely
                // the conflation this change exists to remove, so the catch must not reintroduce it.
                logger.LogDebug(ex,
                    "GitHub webhook body read failed with {Status}: {Reason}. At 400 the delivery "
                    + "ended before the declared body arrived — nothing to verify, and GitHub "
                    + "retries its own failed deliveries.",
                    ex.StatusCode, ex.Message);
                return Results.StatusCode(ex.StatusCode);
            }

            if (body is null)
            {
                logger.LogWarning(
                    "GitHub webhook body exceeded {MaxBytes} bytes — refused before signature verification.",
                    MaxWebhookBodyBytes);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var signature = http.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!GitHubWebhookProcessor.VerifySignature(secret, body, signature))
            {
                logger.LogWarning("GitHub webhook signature verification failed ({Bytes} bytes).", body.Length);
                return Results.Unauthorized();
            }

            var eventType = http.Request.Headers["X-GitHub-Event"].ToString();
            if (string.Equals(eventType, "ping", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { ok = true, pong = true });

            using var doc = ParseOrNull(body);
            if (doc is null)
            {
                logger.LogWarning("GitHub webhook body was not valid JSON.");
                return Results.BadRequest();
            }

            // Process() reads the payload synchronously into materialized records before returning
            // its observable, so the JsonDocument may be disposed once this handler returns. Bridge
            // to Task ONCE here at the HTTP boundary, through ObserveCompletion rather than Rx's
            // .ToTask() (which resumes inline on the signalling thread — forbidden since
            // 2026-08-30); a processing failure is logged, never 500-thrown.
            var updated = await processor.Process(eventType, doc.RootElement)
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "GitHub webhook processing failed for event {Event}.", eventType);
                    return Observable.Return(0);
                })
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger.LogWarning(ex,
                        "GitHub webhook processing for event {Event} faulted after the response had "
                        + "already been sent", eventType),
                    http.RequestAborted);

            return Results.Ok(new { ok = true, updated });
        });

        return endpoints;
    }

    private static JsonDocument? ParseOrNull(byte[] body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }
}
