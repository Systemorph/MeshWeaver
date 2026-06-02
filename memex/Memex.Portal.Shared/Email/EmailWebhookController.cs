using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Receives Microsoft Graph change notifications for the mailbox inbox. Anonymous (Graph posts are
/// unauthenticated and validated by the shared <c>clientState</c>) — <c>/api/email</c> is in the
/// onboarding middleware's excluded prefixes and outside the MCP bearer policy.
///
/// <para>Two shapes: the subscription-creation handshake (<c>?validationToken=…</c> → echo it back as
/// text/plain), and notification batches (validate clientState, hand each message id to
/// <see cref="EmailInboundProcessor"/> via a fire-and-forget Subscribe, return 202 immediately).</para>
/// </summary>
[ApiController]
[Route("api/email")]
public sealed class EmailWebhookController(
    EmailInboundProcessor processor,
    EmailOptions options,
    ILogger<EmailWebhookController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [HttpPost]
    public async Task<IActionResult> Post([FromQuery] string? validationToken)
    {
        // 1) Subscription-creation handshake — echo the token within 10s as text/plain.
        if (!string.IsNullOrEmpty(validationToken))
            return Content(validationToken, "text/plain");

        // 2) Notification batch.
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync();

        GraphNotificationBatch? batch;
        try { batch = JsonSerializer.Deserialize<GraphNotificationBatch>(json, JsonOpts); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EmailWebhook: unparseable notification body");
            return BadRequest();
        }

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

        return Accepted();
    }

    // --- Graph notification payload shape ---
    public sealed class GraphNotificationBatch
    {
        [JsonPropertyName("value")] public List<GraphNotification>? Value { get; set; }
    }

    public sealed class GraphNotification
    {
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; set; }
        [JsonPropertyName("clientState")] public string? ClientState { get; set; }
        [JsonPropertyName("resourceData")] public GraphResourceData? ResourceData { get; set; }
    }

    public sealed class GraphResourceData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
