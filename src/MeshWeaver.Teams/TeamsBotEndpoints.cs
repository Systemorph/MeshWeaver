using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Teams;

/// <summary>
/// Bot Framework messaging endpoint for the Teams channel — <c>POST /api/teams/messages</c>.
///
/// <para><b>Anonymous at the pipeline level, authenticated in the handler.</b> Every request is
/// validated by <see cref="ITeamsClient.ValidateInboundAsync"/> (Bot Framework JWT) before anything
/// happens, so a forged POST cannot trigger agent work. The pipeline-level opt-out is explicit
/// because module endpoint contributions map inside a group that defaults to
/// <c>RequireAuthorization()</c> — Bot Framework presents its own bearer token, not a portal
/// session, so the group policy would reject every legitimate call.</para>
///
/// <para>Message activities are parsed and routed to <see cref="TeamsInboundProcessor"/>; the reply
/// is delivered asynchronously by <see cref="TeamsReplySender"/>. Anything that is not a
/// <c>message</c> (typing, conversationUpdate, …) is acknowledged and ignored.</para>
///
/// <para>This was an MVC controller while it lived in the portal; it is a minimal-API endpoint here
/// because the module endpoint hook maps route handlers, not controllers. Same route, verb, guard
/// order and status codes — including the <c>404</c> when Teams is unconfigured, which is what
/// keeps an un-provisioned deployment from answering the Bot Framework at all.</para>
/// </summary>
public static class TeamsBotEndpoints
{
    /// <summary>The Bot Framework messaging route.</summary>
    public const string Route = "/api/teams/messages";

    /// <summary>Maps <c>POST /api/teams/messages</c>.</summary>
    public static IEndpointRouteBuilder MapTeamsBot(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, async (HttpContext http, CancellationToken ct) =>
        {
            var teamsClient = http.RequestServices.GetRequiredService<ITeamsClient>();
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(TeamsBotEndpoints));

            if (!teamsClient.IsConfigured) return Results.NotFound();
            if (!await teamsClient.ValidateInboundAsync(http.Request.Headers.Authorization.ToString(), ct))
                return Results.Unauthorized();

            string body;
            using (var reader = new StreamReader(http.Request.Body))
                body = await reader.ReadToEndAsync(ct);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (GetString(root, "type") != "message") return Results.Ok();   // typing/conversationUpdate/etc.

                var from = root.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
                var conversation = root.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

                var msg = new InboundTeamsMessage(
                    Text: StripMentions(GetString(root, "text") ?? ""),
                    ConversationId: GetString(conversation, "id") ?? "",
                    ServiceUrl: GetString(root, "serviceUrl") ?? "",
                    AadObjectId: GetString(from, "aadObjectId"),
                    UserName: GetString(from, "name"));

                if (!string.IsNullOrWhiteSpace(msg.Text) && !string.IsNullOrEmpty(msg.ConversationId))
                {
                    var processor = http.RequestServices.GetRequiredService<TeamsInboundProcessor>();
                    processor.Route(msg).Subscribe(
                        _ => { },
                        ex => logger.LogWarning(ex, "Teams: routing failed"));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Teams: malformed activity payload");
            }

            return Results.Ok();   // Bot Framework expects a prompt 200/202; the agent reply is proactive.
        })
        .AllowAnonymous();

        return endpoints;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // Teams channel messages carry the bot @-mention as <at>Name</at> markup — strip it.
    private static string StripMentions(string text) =>
        Regex.Replace(text, "<at>.*?</at>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();
}
