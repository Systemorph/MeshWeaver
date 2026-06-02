using System.ComponentModel;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Graph.Users.Item.Messages.Item.Reply;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// <b>Executive Assistant</b> agent tool: read/write access to the <i>signed-in user's own</i> mailbox
/// and calendar via Microsoft Graph (app-only credential, scoped to <c>users/{me}</c>). Lets an EA agent
/// triage mail, write/reply to mail, and read/create/cancel calendar events ("do my booking").
///
/// <para>The acting user is resolved from <see cref="AccessService"/> at call time — the agent runs under
/// the user's identity, so every Graph call targets that user's mailbox. Requires the application Graph
/// permissions <c>Mail.ReadWrite</c>, <c>Mail.Send</c> and <c>Calendars.ReadWrite</c> (admin-consented).</para>
///
/// <para>These methods are agent tools (the Microsoft.Extensions.AI boundary), so <c>async</c>/<c>await</c>
/// is correct here — this is not hub-reachable code.</para>
/// </summary>
public sealed class ExecutiveAssistantPlugin : IAgentPlugin
{
    private readonly EmailOptions _options;
    private readonly AccessService _access;
    private readonly ILogger<ExecutiveAssistantPlugin>? _logger;
    private readonly Lazy<GraphServiceClient> _graph;

    public string Name => "ExecutiveAssistant";

    public ExecutiveAssistantPlugin(
        EmailOptions options, AccessService access, ILogger<ExecutiveAssistantPlugin>? logger = null)
    {
        _options = options;
        _access = access;
        _logger = logger;
        _graph = new Lazy<GraphServiceClient>(() =>
        {
            TokenCredential credential = options.UseManagedIdentity
                ? new DefaultAzureCredential()
                : new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
            return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        });
    }

    private GraphServiceClient Graph => _graph.Value;

    /// <summary>The mailbox/calendar owner = the user the agent is acting for (oid or UPN, both valid for Graph).</summary>
    private string? Me => _access.Context?.ObjectId ?? _access.Context?.Name
                          ?? _access.CircuitContext?.ObjectId ?? _access.CircuitContext?.Name;

    // ---- Email ------------------------------------------------------------

    [Description("Lists the signed-in user's most recent inbox emails (newest first) with id, from, subject, received time, and a short preview.")]
    public async Task<string> ListInbox([Description("How many messages to return (default 10, max 50)")] int count = 10)
    {
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            var page = await Graph.Users[me].MailFolders["inbox"].Messages.GetAsync(rc =>
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
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            var page = await Graph.Users[me].Messages.GetAsync(rc =>
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
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            var m = await Graph.Users[me].Messages[id].GetAsync(rc =>
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
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            await Graph.Users[me].SendMail.PostAsync(new SendMailPostRequestBody
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

    [Description("Replies to one of the signed-in user's emails by id (reply-all not used; replies to sender).")]
    public async Task<string> ReplyToMail(
        [Description("Message id to reply to")] string id,
        [Description("Reply body")] string body)
    {
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            await Graph.Users[me].Messages[id].Reply.PostAsync(new ReplyPostRequestBody { Comment = body });
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
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            var start = ParseOrDefault(startUtc, DateTimeOffset.UtcNow);
            var end = ParseOrDefault(endUtc, start.AddDays(7));
            var page = await Graph.Users[me].CalendarView.GetAsync(rc =>
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
        if (Me is not { } me) return "No signed-in user to act for.";
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
            var created = await Graph.Users[me].Events.PostAsync(ev);
            return $"Created event '{subject}' (id {created?.Id}).";
        }
        catch (Exception ex) { return Fail(nameof(CreateEvent), ex); }
    }

    [Description("Cancels/deletes a calendar event on the signed-in user's calendar by id.")]
    public async Task<string> CancelEvent([Description("Event id")] string id)
    {
        if (Me is not { } me) return "No signed-in user to act for.";
        try
        {
            await Graph.Users[me].Events[id].DeleteAsync();
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

    private string Fail(string op, Exception ex)
    {
        _logger?.LogWarning(ex, "ExecutiveAssistant.{Op} failed", op);
        return $"{op} failed: {ex.Message}";
    }

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
}
