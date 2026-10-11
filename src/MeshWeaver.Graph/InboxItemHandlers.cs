using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The inbox handlers core itself brings — for the two item types core owns. Every other type's
/// handler arrives with the package that owns the type (the Threads app brings the thread's).
/// </summary>
public static class InboxItemHandlers
{
    /// <summary>
    /// The handler for <paramref name="nodeType"/> among <paramref name="handlers"/>, or null when
    /// nobody brought one. Ordinal: a node type is an identifier, never display text.
    /// </summary>
    /// <param name="handlers">The registered handlers.</param>
    /// <param name="nodeType">The row's node type.</param>
    public static IInboxItemHandler? For(IEnumerable<IInboxItemHandler> handlers, string? nodeType)
        => nodeType is null
            ? null
            : handlers.FirstOrDefault(h => string.Equals(h.NodeType, nodeType, StringComparison.Ordinal));

    /// <summary>
    /// One typed write as an outcome: the node's content read as <typeparamref name="TContent"/>,
    /// changed, written through the owning hub. A fault becomes the item's own answer — a bulk act
    /// names what happened to each item rather than aborting on the first refusal.
    ///
    /// <para>🚨 The fault is LOGGED here and the outcome carries NO reason: a typed-write failure can
    /// quote the content it could not read, and an outcome's reason is shown to the viewer verbatim.
    /// The page says "could not be changed"; the log says why.</para>
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="path">The item's node path.</param>
    /// <param name="change">The change; returns its argument unchanged when the item may not be changed.</param>
    /// <param name="reached">Whether the content AS WRITTEN is in the state the action asked for.</param>
    /// <remarks>
    /// 🚨 <c>Done</c> needs BOTH: this call changed the content, and the content is now in the
    /// asked-for state. "The update completed" proves neither — a no-op completes like a real write.
    /// And the final state alone is not enough: an item someone ELSE already put away is in the
    /// asked-for state too, and counting it would put it in this click's Undo batch, where Undo
    /// would reverse the other writer's act. The update lambda can run more than once (a retried
    /// write), so the flag records its LAST run — the one whose result was written.
    /// </remarks>
    internal static IObservable<InboxItemOutcome> Write<TContent>(
        IMessageHub hub, string path, Func<TContent, TContent> change, Func<TContent, bool> reached) where TContent : class
        => Observable.Defer(() =>
        {
            var changed = false;
            return hub.GetMeshNodeStream(path).Update<TContent>(current =>
                {
                    var next = change(current);
                    changed = !Equals(next, current);
                    return next;
                })
                .Take(1)
                .Select(node => new InboxItemOutcome(path,
                    changed && node.ContentAs<TContent>(hub.JsonSerializerOptions) is { } content && reached(content)));
        })
            .Catch((Exception ex) =>
            {
                hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InboxItemHandlers))
                    .LogWarning(ex, "Inbox: {Path} could not be written", path);
                return Observable.Return(new InboxItemOutcome(path, false));
            });
}

/// <summary>
/// Notifications: Delete sets <see cref="Notification.Dismissed"/>, Undo clears it. The node stays —
/// retention removes it, exactly as it would have (Doc/Architecture/NotificationRetention).
/// </summary>
public sealed class NotificationInboxItemHandler : IInboxItemHandler
{
    /// <inheritdoc />
    public string NodeType => NotificationNodeType.NodeType;

    /// <inheritdoc />
    public IObservable<InboxItemOutcome> Dismiss(IMessageHub hub, string path)
        => InboxItemHandlers.Write<Notification>(hub, path, n => n with { Dismissed = true }, n => n.Dismissed);

    /// <inheritdoc />
    public IObservable<InboxItemOutcome> Restore(IMessageHub hub, string path)
        => InboxItemHandlers.Write<Notification>(hub, path, n => n with { Dismissed = false }, n => !n.Dismissed);
}

/// <summary>
/// The user's stored mail (<c>{user}/_Email</c>): Delete archives it, Undo returns it to
/// <see cref="EmailStatus.Read"/> — the viewer has, by then, seen it.
///
/// <para>Only an INBOUND mail that is not in flight is touched. An outbound mail's
/// <see cref="Email.Status"/> is its send state (<see cref="EmailStatus.Sending"/> /
/// <see cref="EmailStatus.Sent"/> / <see cref="EmailStatus.Failed"/>), which the sender watches;
/// archiving one would stop a queued mail from ever leaving.</para>
/// </summary>
public sealed class EmailInboxItemHandler : IInboxItemHandler
{
    /// <inheritdoc />
    public string NodeType => EmailNodeType.NodeType;

    /// <inheritdoc />
    public IObservable<InboxItemOutcome> Dismiss(IMessageHub hub, string path)
        => InboxItemHandlers.Write<Email>(hub, path,
            mail => CanFile(mail) ? mail with { Status = EmailStatus.Archived } : mail,
            // Done only when THIS mail is now a filed inbound one — an outbound mail, or one the
            // write left alone, was not put away and must never enter the Undo batch.
            mail => mail is { Direction: EmailDirection.Inbound, Status: EmailStatus.Archived });

    /// <inheritdoc />
    public IObservable<InboxItemOutcome> Restore(IMessageHub hub, string path)
        => InboxItemHandlers.Write<Email>(hub, path,
            mail => mail is { Direction: EmailDirection.Inbound, Status: EmailStatus.Archived }
                ? mail with { Status = EmailStatus.Read }
                : mail,
            mail => mail is { Direction: EmailDirection.Inbound, Status: EmailStatus.New or EmailStatus.Read });

    /// <summary>Whether <paramref name="mail"/> may be archived from the inbox — inbound, and not already archived.</summary>
    internal static bool CanFile(Email mail)
        => mail is { Direction: EmailDirection.Inbound, Status: EmailStatus.New or EmailStatus.Read };
}
