using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Teams;

/// <summary>
/// Bot Framework messaging endpoint for the Teams channel. Anonymous at the pipeline level, but every
/// request is authenticated by <see cref="ITeamsClient.ValidateInboundAsync"/> (Bot Framework JWT) before
/// anything happens — so a forged POST can't trigger agent work. Message activities are parsed and routed
/// to <see cref="TeamsInboundProcessor"/>; the reply is delivered asynchronously by the reply sender.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/teams")]
public sealed class TeamsBotController(
    ITeamsClient teamsClient, TeamsInboundProcessor processor, ILogger<TeamsBotController> logger) : ControllerBase
{
    [HttpPost("messages")]
    public async Task<IActionResult> Messages(CancellationToken ct)
    {
        if (!teamsClient.IsConfigured) return NotFound();
        if (!await teamsClient.ValidateInboundAsync(Request.Headers.Authorization.ToString(), ct))
            return Unauthorized();

        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (GetString(root, "type") != "message") return Ok();   // ignore typing/conversationUpdate/etc.

            var from = root.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var conversation = root.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

            var msg = new InboundTeamsMessage(
                Text: StripMentions(GetString(root, "text") ?? ""),
                ConversationId: GetString(conversation, "id") ?? "",
                ServiceUrl: GetString(root, "serviceUrl") ?? "",
                AadObjectId: GetString(from, "aadObjectId"),
                UserName: GetString(from, "name"));

            if (!string.IsNullOrWhiteSpace(msg.Text) && !string.IsNullOrEmpty(msg.ConversationId))
                processor.Route(msg).Subscribe(
                    _ => { },
                    ex => logger.LogWarning(ex, "Teams: routing failed"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Teams: malformed activity payload");
        }

        return Ok();   // Bot Framework expects a prompt 200/202; the agent reply is sent proactively.
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // Teams channel messages carry the bot @-mention as <at>Name</at> markup — strip it.
    private static string StripMentions(string text) =>
        Regex.Replace(text, "<at>.*?</at>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();
}
