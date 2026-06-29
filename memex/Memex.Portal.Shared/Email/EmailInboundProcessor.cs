using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Routes an inbound email by sender, with all matching driven through <see cref="IMeshQueryCore"/> —
/// no in-memory registries.
/// <list type="bullet">
///   <item><b>email → user:</b> structured exact query <c>nodeType:User content.email:{from}</c>.</item>
///   <item><b>email → conversation:</b> the subject is matched via the vector index (bare-text query
///     scoped to the sender's mail), and the nearest prior email is <i>confirmed</i> as the same
///     conversation by comparing the normalized-subject key (reply/forward prefixes stripped). One
///     thread per conversation: a match continues that thread, otherwise a new one is started.</item>
/// </list>
/// A non-user's mail goes to the admin inbox (<c>Admin/Inbox</c>) with an admin notification. Every
/// message is persisted as an <see cref="MeshWeaver.Mesh.Email"/> node and then marked read.
///
/// <para><b>100% reactive</b> (no <c>await</c>); Graph HTTP I/O is pooled in <see cref="GraphMail"/>;
/// writes wrap in <c>ImpersonateAsSystem</c>. <see cref="Route"/> is Graph-free + public so the
/// routing/threading is unit-testable. The agent's reply is sent back by the separate
/// <c>OutboundEmailSender</c> (mesh-driven, see that type) — this processor only does intake.</para>
/// </summary>
public sealed class EmailInboundProcessor(
    IMessageHub hub,
    GraphMail graphMail,
    ILogger<EmailInboundProcessor>? logger = null)
{
    /// <summary>The dedicated email parser agent (see <c>Agent/EmailRouter.md</c>).</summary>
    public const string ParserAgent = "EmailRouter";

    private readonly IMeshService meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
    private readonly AccessService accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
    private readonly IMeshQueryCore query = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();

    /// <summary>A parsed inbound email — the Graph-free input to <see cref="Route"/>.</summary>
    public sealed record InboundMessage(
        string From, string? FromName, string Subject, string Body,
        string? ConversationId, string? InternetMessageId);

    /// <summary>Fetch the message, route it, mark it read. Cold; subscribe to drive.</summary>
    public IObservable<Unit> ProcessNotification(string messageId) =>
        graphMail.GetMessage(messageId)
            .SelectMany(msg =>
            {
                if (msg is null) return Observable.Return(Unit.Default);
                var inbound = new InboundMessage(
                    From: msg.From?.EmailAddress?.Address?.Trim() ?? "",
                    FromName: msg.From?.EmailAddress?.Name,
                    Subject: msg.Subject ?? "(no subject)",
                    Body: msg.Body?.Content ?? "",
                    ConversationId: msg.ConversationId,
                    InternetMessageId: msg.InternetMessageId);
                return Route(inbound).SelectMany(_ => graphMail.MarkRead(messageId));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "EmailInbound: failed to process message {MessageId}", messageId);
                return Observable.Return(Unit.Default);
            });

    /// <summary>Routes a parsed message by sender (no Graph). Unit-testable.</summary>
    public IObservable<Unit> Route(InboundMessage m)
    {
        if (string.IsNullOrEmpty(m.From)) return Observable.Return(Unit.Default);
        // Loop guard: never act on our own mail.
        if (string.Equals(m.From, graphMail.Mailbox, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(Unit.Default);

        // email → user: structured exact match through IMeshQueryCore.
        return query.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"nodeType:{UserNodeType.NodeType} content.email:{m.From} limit:1"),
                hub.JsonSerializerOptions)
            .Take(1)
            .Select(change => change.Items.FirstOrDefault(n => n.State == MeshNodeState.Active))
            .SelectMany(userNode => userNode is not null
                ? HandleUser(userNode.Id, m)
                : HandleNonUser(m));
    }

    // ── Known user → email node + (matched | new) conversation thread + notification ──
    private IObservable<Unit> HandleUser(string username, InboundMessage m)
    {
        var key = ThreadKey(m.Subject);
        var emailNs = $"{username}/{EmailNodeType.UserEmailSegment}";

        return FindConversationThread(username, m.Subject, key).SelectMany(matchedThreadPath =>
            Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ =>
                {
                    var text = "Process this inbound email and reply to the sender.\n\nEmail: @/{0}";
                    if (!string.IsNullOrEmpty(matchedThreadPath))
                    {
                        // Same conversation → continue its thread + record the inbound email.
                        var emailNode = BuildEmailNode(emailNs, m, EmailStatus.Read, matchedThreadPath, key);
                        return meshService.CreateNode(emailNode).SelectMany(saved =>
                        {
                            hub.SubmitMessage(matchedThreadPath, string.Format(text, saved.Path),
                                agentName: ParserAgent, contextPath: saved.Path,
                                createdBy: username, authorName: m.FromName ?? m.From,
                                onError: err => logger?.LogWarning("EmailInbound: SubmitMessage failed: {Err}", err));
                            return CreateNotification(username, matchedThreadPath, m);
                        });
                    }

                    // New conversation → create email, then a thread with the email as MainNode.
                    var newEmail = BuildEmailNode(emailNs, m, EmailStatus.Read, threadPath: null, key);
                    return meshService.CreateNode(newEmail).SelectMany(saved =>
                        StartThreadRx(username, string.Format(text, saved.Path), saved.Path).SelectMany(thread =>
                            // backfill the email's ThreadPath now that the thread exists
                            meshService.UpdateNode(WithThreadPath(saved, thread.Path))
                                .SelectMany(_ => CreateNotification(username, thread.Path, m))));
                }));
    }

    // ── Non-user → admin inbox + admin notification (no agent) ────────────────
    private IObservable<Unit> HandleNonUser(InboundMessage m)
    {
        var emailNode = BuildEmailNode(EmailNodeType.AdminInboxNamespace, m, EmailStatus.New,
            threadPath: null, ThreadKey(m.Subject));
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(emailNode)
                .SelectMany(saved => CreateNotification("Admin", saved.Path, m))
                .Do(_ => logger?.LogInformation("EmailInbound: filed non-user mail from {From} to {Inbox}",
                    m.From, EmailNodeType.AdminInboxNamespace)));
    }

    /// <summary>
    /// Vector-matches the subject against the sender's prior mail (HNSW when an embedding provider is
    /// present, ILIKE fallback otherwise — see <c>VectorSearch.md</c>), then <b>confirms</b> the
    /// candidate is the same conversation by normalized-subject key. Returns the existing thread path,
    /// or null to start a new conversation.
    /// </summary>
    private IObservable<string?> FindConversationThread(string username, string subject, string key)
    {
        var safeSubject = SanitizeForQuery(subject);
        var q = $"{safeSubject} nodeType:{EmailNodeType.NodeType} namespace:{username}/{EmailNodeType.UserEmailSegment} limit:10";
        var jsonOptions = hub.JsonSerializerOptions;
        return query.Query<MeshNode>(MeshQueryRequest.FromQuery(q), jsonOptions)
            .Take(1)
            .Select(change => change.Items
                .Select(n => EmailOf(n, jsonOptions))
                .Where(e => e is { ThreadPath: { Length: > 0 } } && e.ThreadKey == key)
                .Select(e => e!.ThreadPath)
                .FirstOrDefault());
    }

    /// <summary>Bridges <see cref="HubThreadExtensions.StartThread"/>'s onCreated callback to an observable.</summary>
    private IObservable<MeshNode> StartThreadRx(string username, string text, string emailPath) =>
        Observable.Create<MeshNode>(observer =>
        {
            hub.StartThread(username, text,
                agentName: ParserAgent,
                contextPath: emailPath,
                mainNode: emailPath,
                createdBy: username,
                onCreated: node => { observer.OnNext(node); observer.OnCompleted(); },
                onError: err => observer.OnError(new InvalidOperationException(err)));
            return Disposable.Empty;
        });

    private MeshNode BuildEmailNode(
        string namespacePath, InboundMessage m, EmailStatus status, string? threadPath, string threadKey) =>
        new(Guid.NewGuid().ToString("N"), namespacePath)
        {
            Name = m.Subject,
            NodeType = EmailNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MeshWeaver.Mesh.Email
            {
                Direction = EmailDirection.Inbound,
                From = m.From,
                FromName = m.FromName,
                To = graphMail.Mailbox,
                Subject = m.Subject,
                Body = m.Body,
                ConversationId = m.ConversationId,
                InternetMessageId = m.InternetMessageId,
                Status = status,
                ThreadPath = threadPath,
                ThreadKey = threadKey,
            }
        };

    private static MeshNode WithThreadPath(MeshNode emailNode, string threadPath) =>
        emailNode.Content is MeshWeaver.Mesh.Email e
            ? emailNode with { Content = e with { ThreadPath = threadPath } }
            : emailNode;

    /// <summary>Creates a bell Notification under <c>{ownerPartition}/_Notification</c> as System.</summary>
    private IObservable<Unit> CreateNotification(string ownerPartition, string targetPath, InboundMessage m)
    {
        var node = new MeshNode(Guid.NewGuid().ToString("N"), $"{ownerPartition}/_Notification")
        {
            NodeType = "Notification",
            Name = m.Subject,
            MainNode = targetPath,
            State = MeshNodeState.Active,
            Content = new MeshWeaver.Mesh.Notification
            {
                Title = $"New email from {m.FromName ?? m.From}",
                Message = m.Subject,
                TargetNodePath = targetPath,
                NotificationType = MeshWeaver.Mesh.NotificationType.General,
                CreatedBy = m.From,
            }
        };
        return meshService.CreateNode(node).Select(_ => Unit.Default);
    }

    private static MeshWeaver.Mesh.Email? EmailOf(MeshNode n, System.Text.Json.JsonSerializerOptions? options) =>
        n.Content switch
        {
            MeshWeaver.Mesh.Email e => e,
            System.Text.Json.JsonElement je => SafeDeserialize(je, options),
            _ => null
        };

    private static MeshWeaver.Mesh.Email? SafeDeserialize(System.Text.Json.JsonElement je, System.Text.Json.JsonSerializerOptions? options)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<MeshWeaver.Mesh.Email>(je.GetRawText(), options); }
        catch { return null; }
    }

    // ── Subject normalization → stable conversation key ───────────────────────
    // Strips ANY number of leading reply/forward markers (several languages), e.g.
    // "Re: Fwd: AW: Re[2]: Hello" → "hello".
    private static readonly Regex ReplyPrefix = new(
        @"^\s*(re|fwd|fw|aw|wg|tr|rv|sv|vs|antw|antwort|rif)(\s*\[\d+\])?\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Normalizes a subject (strip reply/forward prefixes) and slugifies it into a stable key.</summary>
    public static string ThreadKey(string subject)
    {
        var s = subject ?? "";
        string prev;
        do { prev = s; s = ReplyPrefix.Replace(s, ""); } while (s != prev);
        s = s.Trim().ToLowerInvariant();
        var slug = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        slug = Regex.Replace(slug, "-{2,}", "-").Trim('-');
        if (slug.Length == 0) slug = "email";
        return slug.Length > 64 ? slug[..64].Trim('-') : slug;
    }

    /// <summary>Strips characters that the mesh-query parser would misread (so the subject stays bare-text).</summary>
    private static string SanitizeForQuery(string subject)
    {
        var normalized = subject ?? "";
        string prev;
        do { prev = normalized; normalized = ReplyPrefix.Replace(normalized, ""); } while (normalized != prev);
        var chars = normalized.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray();
        return Regex.Replace(new string(chars), @"\s{2,}", " ").Trim();
    }
}
