using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mail.MicrosoftGraph;

/// <summary>
/// Receives Microsoft Graph change notifications for the mailbox inbox — <c>POST /api/email</c>.
///
/// <para>Two shapes: the subscription-creation handshake (<c>?validationToken=…</c> → echo it back
/// as <c>text/plain</c> within 10 s), and notification batches (validate <c>clientState</c>, hand
/// each message id to <see cref="EmailInboundProcessor"/> via a fire-and-forget Subscribe, ack
/// 202 immediately).</para>
///
/// <para><b>Anonymous by design, and it must stay so.</b> Graph posts these notifications
/// unauthenticated; the shared <c>clientState</c> secret IS the guard. Module endpoint
/// contributions map inside a group defaulting to <c>RequireAuthorization()</c>, so the opt-out is
/// explicit — the same shape as the LinkedIn OAuth callbacks. <c>/api/email</c> is also in the
/// onboarding middleware's excluded prefixes (host-side, unchanged by this move).</para>
///
/// <para>This was an MVC controller while it lived in the portal; it is a minimal-API endpoint here
/// because the module endpoint hook maps route handlers, not controllers — a module's controller
/// would additionally need its assembly registered as an MVC ApplicationPart. Behaviour is
/// identical: same route, same verb, same two shapes, same status codes.</para>
/// </summary>
public static class EmailWebhookEndpoints
{
    /// <summary>The route Microsoft Graph posts change notifications to.</summary>
    public const string Route = "/api/email";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Maps <c>POST /api/email</c>.</summary>
    public static IEndpointRouteBuilder MapEmailWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, async (HttpContext http, string? validationToken) =>
        {
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(EmailWebhookEndpoints));

            // 1) Subscription-creation handshake — echo the token as text/plain.
            if (!string.IsNullOrEmpty(validationToken))
                return Results.Text(validationToken, "text/plain");

            // 2) Notification batch.
            string json;
            using (var reader = new StreamReader(http.Request.Body))
                json = await reader.ReadToEndAsync(http.RequestAborted);

            GraphNotificationBatch? batch;
            try { batch = JsonSerializer.Deserialize<GraphNotificationBatch>(json, JsonOpts); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "EmailWebhook: unparseable notification body");
                return Results.BadRequest();
            }

            var options = http.RequestServices.GetRequiredService<EmailOptions>();
            var processor = http.RequestServices.GetRequiredService<EmailInboundProcessor>();

            foreach (var n in batch?.Value ?? [])
            {
                if (!string.Equals(n.ClientState, options.SubscriptionClientState, StringComparison.Ordinal))
                {
                    logger.LogWarning("EmailWebhook: clientState mismatch — ignoring notification");
                    continue;
                }
                var messageId = n.ResourceData?.Id;
                if (string.IsNullOrEmpty(messageId)) continue;

                // Fire-and-forget: process off the request thread; we ack immediately.
                processor.ProcessNotification(messageId).Subscribe(
                    _ => { },
                    ex => logger.LogWarning(ex, "EmailWebhook: processing failed for {MessageId}", messageId));
            }

            return Results.Accepted();
        })
        .AllowAnonymous();

        return endpoints;
    }

    // --- Graph notification payload shape ---

    /// <summary>A batch of Graph change notifications.</summary>
    public sealed class GraphNotificationBatch
    {
        /// <summary>The notifications in this batch.</summary>
        [JsonPropertyName("value")] public List<GraphNotification>? Value { get; set; }
    }

    /// <summary>One Graph change notification.</summary>
    public sealed class GraphNotification
    {
        /// <summary>The subscription that produced this notification.</summary>
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; set; }

        /// <summary>The shared secret echoed back by Graph — the guard on this anonymous route.</summary>
        [JsonPropertyName("clientState")] public string? ClientState { get; set; }

        /// <summary>The changed resource's identifiers.</summary>
        [JsonPropertyName("resourceData")] public GraphResourceData? ResourceData { get; set; }
    }

    /// <summary>The changed resource's identifiers.</summary>
    public sealed class GraphResourceData
    {
        /// <summary>The message id.</summary>
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
