using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One inbox item as its source READ it for the reading view — what the item says, never stored.
/// A mail is a subject, a sender and a <see cref="Body"/>; a chat is a list of
/// <see cref="Messages"/>. Every text is PLAIN TEXT: the inbox renders it escaped, so a source
/// never hands the portal markup it would run.
/// </summary>
public sealed record InboxItemContent
{
    /// <summary>What the item is called — a mail's subject, a chat's topic.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Who it is from, as the source names them.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Which of the viewer's accounts it reached; empty when the source does not say.</summary>
    public string Account { get; init; } = string.Empty;

    /// <summary>When it arrived (UTC); the inbox shows it in the viewer's zone.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>The item's text, plain; empty for an item that is only <see cref="Messages"/>.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>A conversation's messages, oldest first; empty for a single mail.</summary>
    public ImmutableList<InboxItemMessage> Messages { get; init; } = [];

    /// <summary>
    /// The item in its provider's own client — offered as a SECONDARY link under the content, never
    /// where a click on the row goes. An https URL, or empty.
    /// </summary>
    public string WebLink { get; init; } = string.Empty;

    /// <summary>The provider's name for the link ("Outlook", "Gmail", "Teams"); empty for a generic label.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Why the item cannot be shown, when it cannot — shown VERBATIM, so a refusal the reader chose
    /// to state ("Outlook is not connected — …"), never an exception's message. Null when it can.
    /// </summary>
    public string? Refusal { get; init; }
}

/// <summary>One message of a conversation in the reading view.</summary>
/// <param name="From">Who wrote it.</param>
/// <param name="SentAt">When (UTC); the inbox shows it in the viewer's zone.</param>
/// <param name="Body">Its text, plain.</param>
public sealed record InboxItemMessage(string From, DateTimeOffset? SentAt, string Body);

/// <summary>
/// How the inbox READS one item of a node type for its reading view — the third part of the potluck
/// beside <see cref="InboxQueryLeg"/> (what is listed) and <see cref="IInboxItemHandler"/> (what may
/// be done). A click on a row ALWAYS opens the item inside the portal
/// (<c>/{owner}/Inbox/{item}</c>); a source whose items live outside the mesh — a mailbox, a chat
/// service — brings a reader that fetches the content LIVE, and the inbox shows it. Nothing it
/// returns is stored.
///
/// <para>Register one as a singleton service. A reader is chosen by the item's PATH
/// (<see cref="Handles"/>) — the only thing the reading view's address carries — so it answers for
/// the reserved namespace its source owns (<c>{user}/_Mailbox/outlook</c>, …). A stored mesh item
/// no reader handles is shown by its own page, embedded in the reading view.</para>
///
/// <para>🚨 <b>Only the owner is answered.</b> The inbox calls a reader only for its owner's own
/// items, and the reader must refuse (<see cref="InboxItemContent.Refusal"/>) when
/// <c>viewer</c> is not the item's owner: nobody's mail is read on anyone else's behalf.</para>
/// </summary>
public interface IInboxItemReader
{
    /// <summary>
    /// Whether <paramref name="path"/> is one of this reader's items — a pure test of the path's
    /// shape, never a read. The first reader that says yes reads it.
    /// </summary>
    /// <param name="path">The item's node path.</param>
    bool Handles(string path);

    /// <summary>
    /// The item at <paramref name="path"/>, read live as <paramref name="viewer"/>. Emits one answer
    /// and completes; a refusal is an answer with <see cref="InboxItemContent.Refusal"/>, a fault is
    /// shown as a generic failure (and should be logged by the reader).
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="path">The item's node path.</param>
    /// <param name="viewer">The person reading — the inbox's owner.</param>
    IObservable<InboxItemContent> Read(IMessageHub hub, string path, string viewer);
}
