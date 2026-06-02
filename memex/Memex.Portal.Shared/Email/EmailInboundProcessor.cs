using System.Reactive;
using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Infrastructure;
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
/// Routes an inbound email (fetched from the mailbox via <see cref="GraphMail"/>) by sender. For a
/// known Memex user the business logic is:
/// <list type="number">
///   <item>Create the <see cref="Email"/> node (<c>CreateNode</c>).</item>
///   <item>Create a thread with that email as <c>MainNode</c>, seeded with a "process this email"
///         message + link, handled by the <b>Email Router</b> parser agent.</item>
///   <item>Create a notification to the user that an email arrived.</item>
/// </list>
/// A non-user's mail is filed into the admin inbox (<c>Admin/Inbox</c>) with an admin notification.
/// Either way the message is then marked read.
///
/// <para><b>100% reactive</b> (<c>Doc/Architecture/AsynchronousCalls.md</c>): every method returns a
/// cold <see cref="IObservable{T}"/>, no <c>await</c>; the Graph HTTP I/O is pooled inside
/// <see cref="GraphMail"/>; writes use the reactive thread/CreateNode APIs and wrap in
/// <c>ImpersonateAsSystem</c> (the external sender carries no identity). Invoked from
/// <c>EmailWebhookController</c>, which subscribes and returns 202 immediately.</para>
/// </summary>
public sealed class EmailInboundProcessor(
    PortalApplication portalApp,
    GraphMail graphMail,
    ILogger<EmailInboundProcessor>? logger = null)
{
    /// <summary>The dedicated email parser agent (see <c>Agent/EmailRouter.md</c>).</summary>
    public const string ParserAgent = "EmailRouter";

    private readonly IMessageHub hub = portalApp.Hub;
    private readonly IMeshService meshService =
        portalApp.Hub.ServiceProvider.GetRequiredService<IMeshService>();
    private readonly AccessService accessService =
        portalApp.Hub.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>Processes one inbound-message notification end-to-end. Cold; subscribe to drive.</summary>
    public IObservable<Unit> ProcessNotification(string messageId) =>
        graphMail.GetMessage(messageId)
            .SelectMany(msg => Route(messageId, msg))
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "EmailInbound: failed to process message {MessageId}", messageId);
                return Observable.Return(Unit.Default);
            });

    private IObservable<Unit> Route(string messageId, Message? msg)
    {
        if (msg is null) return Observable.Return(Unit.Default);

        var from = msg.From?.EmailAddress?.Address?.Trim();
        if (string.IsNullOrEmpty(from)) return Observable.Return(Unit.Default);

        // Loop guard: never act on our own mail.
        if (string.Equals(from, graphMail.Mailbox, StringComparison.OrdinalIgnoreCase))
            return graphMail.MarkRead(messageId);

        var fromName = msg.From?.EmailAddress?.Name;
        var subject = msg.Subject ?? "(no subject)";
        var body = msg.Body?.Content ?? "";
        var conversationId = msg.ConversationId;
        var internetMessageId = msg.InternetMessageId;
        var workspace = hub.GetWorkspace();

        return OnboardingMiddleware.FindUserByEmail(workspace, from, logger)
            .Take(1)
            .SelectMany(userNode => userNode is { State: MeshNodeState.Active }
                ? HandleUser(userNode.Id, from, fromName, subject, body, conversationId, internetMessageId)
                : HandleNonUser(from, fromName, subject, body, conversationId, internetMessageId))
            .SelectMany(_ => graphMail.MarkRead(messageId));
    }

    // ── Known user → email node + thread (email parser agent) + notification ──
    private IObservable<Unit> HandleUser(
        string username, string from, string? fromName, string subject, string body,
        string? conversationId, string? internetMessageId)
    {
        var emailNode = BuildEmailNode($"{username}/{EmailNodeType.UserEmailSegment}",
            from, fromName, subject, body, conversationId, internetMessageId, EmailStatus.Read);

        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(emailNode).Select(saved =>
            {
                var emailPath = saved.Path;
                // 2) Thread with the email as MainNode; the parser agent processes it.
                var text = $"Please process this inbound email and reply to the sender.\n\nEmail: @/{emailPath}";
                hub.StartThread(username, text,
                    agentName: ParserAgent,
                    contextPath: emailPath,
                    mainNode: emailPath,
                    createdBy: username,
                    authorName: fromName ?? from,
                    onCreated: thread =>
                        // 3) Notify the user that mail arrived (links to the thread).
                        CreateNotification($"{username}", thread.Path, from, fromName, subject)
                            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "EmailInbound: user notification failed")),
                    onError: err => logger?.LogWarning("EmailInbound: StartThread failed: {Err}", err));
                return Unit.Default;
            }));
    }

    // ── Non-user → admin inbox + admin notification (no agent) ────────────────
    private IObservable<Unit> HandleNonUser(
        string from, string? fromName, string subject, string body,
        string? conversationId, string? internetMessageId)
    {
        var emailNode = BuildEmailNode(EmailNodeType.AdminInboxNamespace,
            from, fromName, subject, body, conversationId, internetMessageId, EmailStatus.New);

        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(emailNode)
                .SelectMany(saved => CreateNotification("Admin", saved.Path, from, fromName, subject))
                .Do(_ => logger?.LogInformation("EmailInbound: filed non-user mail from {From} to {Inbox}",
                    from, EmailNodeType.AdminInboxNamespace)));
    }

    private static MeshNode BuildEmailNode(
        string namespacePath, string from, string? fromName, string subject, string body,
        string? conversationId, string? internetMessageId, EmailStatus status) =>
        new(Guid.NewGuid().ToString("N"), namespacePath)
        {
            Name = subject,
            NodeType = EmailNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new MeshWeaver.Mesh.Email
            {
                Direction = EmailDirection.Inbound,
                From = from,
                FromName = fromName,
                Subject = subject,
                Body = body,
                ConversationId = conversationId,
                InternetMessageId = internetMessageId,
                Status = status,
            }
        };

    /// <summary>Creates a bell Notification under <c>{ownerPartition}/_Notification</c> as System.</summary>
    private IObservable<Unit> CreateNotification(
        string ownerPartition, string targetPath, string from, string? fromName, string subject)
    {
        var node = new MeshNode(Guid.NewGuid().ToString("N"), $"{ownerPartition}/_Notification")
        {
            NodeType = "Notification",
            Name = subject,
            MainNode = targetPath,
            State = MeshNodeState.Active,
            Content = new MeshWeaver.Mesh.Notification
            {
                Title = $"New email from {fromName ?? from}",
                Message = subject,
                TargetNodePath = targetPath,
                NotificationType = MeshWeaver.Mesh.NotificationType.General,
                CreatedBy = from,
            }
        };
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(node).Select(_ => Unit.Default));
    }
}
