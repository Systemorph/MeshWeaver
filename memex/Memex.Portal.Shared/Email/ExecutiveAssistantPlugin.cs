using System.ComponentModel;
using System.Text.Json;
using Azure.Core;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.AI;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Me.SendMail;
using Microsoft.Graph.Me.Messages.Item.Reply;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// <b>Executive Assistant</b> agent tool: read/write the <i>signed-in user's own</i> mailbox and calendar
/// via Microsoft Graph using a <b>per-user delegated</b> token (the user consents just-in-time — see
/// <see cref="IEaGraphAuth"/>). Every call targets <c>/me</c> with the acting user's own token; there is no
/// standing application-wide Graph access. When the user has not yet connected, each tool returns a consent
/// link instead of acting.
///
/// <para>These methods are agent tools (the Microsoft.Extensions.AI boundary), so <c>async</c>/<c>await</c>
/// is appropriate — not hub-reachable code.</para>
/// </summary>
public sealed class ExecutiveAssistantPlugin(
    IEaGraphAuth ea, AccessService access, EmailOptions options) : IAgentPlugin
{
    public string Name => "ExecutiveAssistant";

    private string? Me => access.Context?.ObjectId ?? access.Context?.Name
                          ?? access.CircuitContext?.ObjectId ?? access.CircuitContext?.Name;

    private string ConsentLink =>
        $"{(options.WebhookBaseUrl ?? "").TrimEnd('/')}/auth/ea/connect";

    /// <summary>Builds a Graph client bound to the acting user's delegated token, or a "please connect" message.</summary>
    private async Task<(GraphServiceClient? graph, string? notConnected)> ClientAsync()
    {
        if (Me is not { } me) return (null, "There is no signed-in user to act for.");
        var token = await ea.GetAccessTokenAsync(me, CancellationToken.None);
        if (string.IsNullOrEmpty(token))
            return (null, "I don't have access to your mailbox and calendar yet. Please connect them here: " +
                          $"{ConsentLink} — it takes a few seconds, then ask me again.");
        return (new GraphServiceClient(new StaticTokenCredential(token!)), null);
    }

    // ---- Email ------------------------------------------------------------

    [Description("Lists the signed-in user's most recent inbox emails (newest first) with id, from, subject, received time, and a short preview.")]
    public async Task<string> ListInbox([Description("How many messages to return (default 10, max 50)")] int count = 10)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            var page = await graph.Me.MailFolders["inbox"].Messages.GetAsync(rc =>
            {
                rc.QueryParameters.Top = Math.Clamp(count, 1, 50);
                rc.QueryParameters.Orderby = ["receivedDateTime desc"];
                rc.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "bodyPreview", "isRead"];
            });
            return Json(page?.Value?.Select(m => new
            {
                id = m.Id,
                from = m.From?.EmailAddress?.Address,
                subject = m.Subject,
                received = m.ReceivedDateTime,
                isRead = m.IsRead,
                preview = m.BodyPreview
            }));
        }
        catch (Exception ex) { return Fail(nameof(ListInbox), ex); }
    }

    [Description("Searches the signed-in user's mailbox by free text (subject/body/sender) and returns matching messages.")]
    public async Task<string> SearchMail(
        [Description("Search text")] string query,
        [Description("How many results (default 10, max 50)")] int count = 10)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            var page = await graph.Me.Messages.GetAsync(rc =>
            {
                rc.QueryParameters.Search = $"\"{query}\"";
                rc.QueryParameters.Top = Math.Clamp(count, 1, 50);
                rc.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "bodyPreview"];
            });
            return Json(page?.Value?.Select(m => new
            {
                id = m.Id, from = m.From?.EmailAddress?.Address, subject = m.Subject,
                received = m.ReceivedDateTime, preview = m.BodyPreview
            }));
        }
        catch (Exception ex) { return Fail(nameof(SearchMail), ex); }
    }

    [Description("Reads the full body of one of the signed-in user's emails by id.")]
    public async Task<string> ReadMail([Description("Message id")] string id)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            var m = await graph.Me.Messages[id].GetAsync(rc =>
                rc.QueryParameters.Select = ["id", "subject", "from", "toRecipients", "receivedDateTime", "body"]);
            return Json(new
            {
                id = m?.Id, from = m?.From?.EmailAddress?.Address,
                to = m?.ToRecipients?.Select(r => r.EmailAddress?.Address),
                subject = m?.Subject, received = m?.ReceivedDateTime, body = m?.Body?.Content
            });
        }
        catch (Exception ex) { return Fail(nameof(ReadMail), ex); }
    }

    [Description("Sends an email from the signed-in user's mailbox.")]
    public async Task<string> SendMail(
        [Description("Recipient address(es), comma-separated")] string to,
        [Description("Subject line")] string subject,
        [Description("Body (HTML allowed)")] string body,
        [Description("Optional CC address(es), comma-separated")] string? cc = null)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            await graph.Me.SendMail.PostAsync(new SendMailPostRequestBody
            {
                Message = new Message
                {
                    Subject = subject,
                    Body = new ItemBody { ContentType = BodyType.Html, Content = body },
                    ToRecipients = Recipients(to),
                    CcRecipients = cc is null ? null : Recipients(cc)
                },
                SaveToSentItems = true
            });
            return $"Sent to {to}.";
        }
        catch (Exception ex) { return Fail(nameof(SendMail), ex); }
    }

    [Description("Replies to one of the signed-in user's emails by id (replies to the sender).")]
    public async Task<string> ReplyToMail(
        [Description("Message id to reply to")] string id,
        [Description("Reply body")] string body)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            await graph.Me.Messages[id].Reply.PostAsync(new ReplyPostRequestBody { Comment = body });
            return "Reply sent.";
        }
        catch (Exception ex) { return Fail(nameof(ReplyToMail), ex); }
    }

    // ---- Calendar ---------------------------------------------------------

    [Description("Lists the signed-in user's calendar events in a window (default: next 7 days). Times are ISO 8601 UTC.")]
    public async Task<string> ListEvents(
        [Description("Window start, ISO 8601 (default: now)")] string? startUtc = null,
        [Description("Window end, ISO 8601 (default: 7 days from start)")] string? endUtc = null)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            var start = ParseOrDefault(startUtc, DateTimeOffset.UtcNow);
            var end = ParseOrDefault(endUtc, start.AddDays(7));
            var page = await graph.Me.CalendarView.GetAsync(rc =>
            {
                rc.QueryParameters.StartDateTime = start.ToString("o");
                rc.QueryParameters.EndDateTime = end.ToString("o");
                rc.QueryParameters.Orderby = ["start/dateTime"];
                rc.QueryParameters.Select = ["id", "subject", "start", "end", "location", "attendees", "isAllDay"];
                rc.QueryParameters.Top = 100;
            });
            return Json(page?.Value?.Select(e => new
            {
                id = e.Id, subject = e.Subject,
                start = e.Start?.DateTime, end = e.End?.DateTime, tz = e.Start?.TimeZone,
                location = e.Location?.DisplayName,
                attendees = e.Attendees?.Select(a => a.EmailAddress?.Address)
            }));
        }
        catch (Exception ex) { return Fail(nameof(ListEvents), ex); }
    }

    [Description("Creates a calendar event ('books' a meeting) on the signed-in user's calendar and invites attendees.")]
    public async Task<string> CreateEvent(
        [Description("Event title")] string subject,
        [Description("Start time, ISO 8601 (UTC unless an offset is given)")] string startIso,
        [Description("End time, ISO 8601")] string endIso,
        [Description("Attendee address(es), comma-separated (optional)")] string? attendees = null,
        [Description("Location (optional)")] string? location = null,
        [Description("Body / agenda (optional)")] string? body = null)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            var ev = new Event
            {
                Subject = subject,
                Start = new DateTimeTimeZone { DateTime = ParseOrDefault(startIso, DateTimeOffset.UtcNow).ToString("o"), TimeZone = "UTC" },
                End = new DateTimeTimeZone { DateTime = ParseOrDefault(endIso, DateTimeOffset.UtcNow.AddHours(1)).ToString("o"), TimeZone = "UTC" },
                Location = location is null ? null : new Location { DisplayName = location },
                Body = body is null ? null : new ItemBody { ContentType = BodyType.Html, Content = body },
                Attendees = string.IsNullOrWhiteSpace(attendees)
                    ? null
                    : attendees.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(a => new Attendee { EmailAddress = new EmailAddress { Address = a }, Type = AttendeeType.Required })
                        .ToList()
            };
            var created = await graph.Me.Events.PostAsync(ev);
            return $"Created event '{subject}' (id {created?.Id}).";
        }
        catch (Exception ex) { return Fail(nameof(CreateEvent), ex); }
    }

    [Description("Cancels/deletes a calendar event on the signed-in user's calendar by id.")]
    public async Task<string> CancelEvent([Description("Event id")] string id)
    {
        var (graph, notConnected) = await ClientAsync();
        if (graph is null) return notConnected!;
        try
        {
            await graph.Me.Events[id].DeleteAsync();
            return "Event cancelled.";
        }
        catch (Exception ex) { return Fail(nameof(CancelEvent), ex); }
    }

    // ---- helpers ----------------------------------------------------------

    private static List<Recipient> Recipients(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new Recipient { EmailAddress = new EmailAddress { Address = a } })
            .ToList();

    private static DateTimeOffset ParseOrDefault(string? iso, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(iso, out var v) ? v : fallback;

    private static string Json(object? value) => JsonSerializer.Serialize(value ?? "none");

    private static string Fail(string op, Exception ex) => $"{op} failed: {ex.Message}";

    public IEnumerable<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(ListInbox),
        AIFunctionFactory.Create(SearchMail),
        AIFunctionFactory.Create(ReadMail),
        AIFunctionFactory.Create(SendMail),
        AIFunctionFactory.Create(ReplyToMail),
        AIFunctionFactory.Create(ListEvents),
        AIFunctionFactory.Create(CreateEvent),
        AIFunctionFactory.Create(CancelEvent)
    ];

    /// <summary>Wraps a pre-fetched delegated access token as a Graph <see cref="TokenCredential"/>.</summary>
    private sealed class StaticTokenCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct)
            => new(token, DateTimeOffset.UtcNow.AddMinutes(50));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct)
            => new(GetToken(requestContext, ct));
    }
}
