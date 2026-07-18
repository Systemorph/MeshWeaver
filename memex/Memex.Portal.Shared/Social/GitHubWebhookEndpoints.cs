using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
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

            // Read the raw body (needed byte-exact for the HMAC).
            byte[] body;
            using (var ms = new MemoryStream())
            {
                await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
                body = ms.ToArray();
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
