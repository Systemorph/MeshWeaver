using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.GitSync;
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
            var body = await BoundedBody.ReadBytesAsync(
                http.Request.Body, MaxWebhookBodyBytes, http.RequestAborted);
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
            // to Task ONCE here at the HTTP boundary; a processing failure is logged, never 500-thrown.
            var updated = await processor.Process(eventType, doc.RootElement)
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "GitHub webhook processing failed for event {Event}.", eventType);
                    return Observable.Return(0);
                })
                .FirstAsync()
                .ToTask(http.RequestAborted);

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
