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
    internal static IObservable<InboxItemOutcome> Write<TContent>(
        IMessageHub hub, string path, Func<TContent, TContent> change) where TContent : class
        => hub.GetMeshNodeStream(path).Update(change)
            .Take(1)
            .Select(_ => new InboxItemOutcome(path, true))
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
        => InboxItemHandlers.Write<Notification>(hub, path, n => n with { Dismissed = true });

    /// <inheritdoc />
    public IObservable<InboxItemOutcome> Restore(IMessageHub hub, string path)
        => InboxItemHandlers.Write<Notification>(hub, path, n => n with { Dismissed = false });
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
        => InboxItemHandlers.Write<Email>(hub, path, mail => CanFile(mail) ? mail with { Status = EmailStatus.Archived } : mail);

    /// <inheritdoc />
    public IObservable<InboxItemOutcome> Restore(IMessageHub hub, string path)
        => InboxItemHandlers.Write<Email>(hub, path,
            mail => mail is { Direction: EmailDirection.Inbound, Status: EmailStatus.Archived }
                ? mail with { Status = EmailStatus.Read }
                : mail);

    /// <summary>Whether <paramref name="mail"/> may be archived from the inbox — inbound, and not already archived.</summary>
    internal static bool CanFile(Email mail)
        => mail is { Direction: EmailDirection.Inbound, Status: EmailStatus.New or EmailStatus.Read };
}
