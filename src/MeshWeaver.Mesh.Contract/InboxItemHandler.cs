using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// What became of ONE inbox item an action was asked for.
/// </summary>
/// <param name="Path">The item's node path.</param>
/// <param name="Done">True when the item is now in the state the action asked for.</param>
/// <param name="Reason">
/// Why not, when <paramref name="Done"/> is false — shown to the viewer VERBATIM, so it is a refusal
/// the handler chose to state (a rule that applies), never an exception's message. Null when the
/// handler has nothing safe to say: the inbox then reports a generic failure, and the handler has
/// logged the cause.
/// </param>
public sealed record InboxItemOutcome(string Path, bool Done, string? Reason = null)
{
    /// <summary>
    /// The step-up this item still waits for, when its decision is recorded but held until the
    /// approver confirms it (core <c>Doc/Architecture/ApprovalStepUp</c>). The inbox collects the
    /// targets of a whole batch into ONE step-up. An init property, never a constructor parameter,
    /// so modules built against the earlier constructor keep binding.
    /// </summary>
    public MeshWeaver.Mesh.Security.StepUpTarget? StepUp { get; init; }
}

/// <summary>
/// What the inbox may DO to the items of one node type — the action half of the potluck whose query
/// half is <see cref="InboxQueryLeg"/>. A provider that brings a leg brings the handler for the type
/// its leg selects; the inbox owns the selection, the buttons and the Undo, and knows nothing about
/// what "put this away" means for a thread, a mail or a notification.
///
/// <para>Register one as a singleton service per node type. A type with no handler is still LISTED
/// (its leg runs), its rows are simply not selectable — the inbox never guesses a write for a type
/// nobody taught it.</para>
///
/// <para>🚨 <b>Dismiss is SOFT, and <see cref="Restore"/> brings the item BACK INTO ITS LEG.</b>
/// The inbox's Delete is Outlook's: the item leaves the list and can be brought back. A handler
/// therefore moves the item into a state its own leg excludes (a closed thread, an archived mail, a
/// dismissed notification) and never deletes a node. Restore is not promised to be the exact
/// inverse — the inbox keeps no record of the prior state — only to return the item to a state its
/// leg selects again: an archived mail comes back as Read whether it was New or Read before. A governed record — an approval, an instance action —
/// has no handler here at all: it leaves the inbox by being decided.</para>
///
/// <para>🚨 <b>Both calls run under the VIEWER's identity, and the write must be issued inside the
/// call</b> — the inbox opens the clicker's access context around the invocation, not around the
/// subscription, because the ambient context does not survive a pool or scheduler hop. A handler
/// that defers its write to a later emission issues it as whoever owns that thread.</para>
/// </summary>
public interface IInboxItemHandler
{
    /// <summary>The node type whose rows this handles — compared ordinally to the row's own.</summary>
    string NodeType { get; }

    /// <summary>
    /// Puts <paramref name="path"/> away: afterwards the provider's own leg no longer selects it.
    /// Emits one outcome and completes; a refusal is an outcome with a reason, never a fault.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="path">The item's node path.</param>
    IObservable<InboxItemOutcome> Dismiss(IMessageHub hub, string path);

    /// <summary>
    /// Brings <paramref name="path"/> back: afterwards the provider's leg selects it again — in a
    /// state the handler chooses, which need not be the one <see cref="Dismiss"/> found. On the same
    /// terms as <see cref="Dismiss"/> otherwise.
    /// </summary>
    /// <param name="hub">The hub the inbox renders on.</param>
    /// <param name="path">The item's node path.</param>
    IObservable<InboxItemOutcome> Restore(IMessageHub hub, string path);
}
