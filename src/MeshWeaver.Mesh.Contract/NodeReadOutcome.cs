namespace MeshWeaver.Mesh;

/// <summary>
/// Why a one-shot node read produced what it produced — the distinction
/// <see cref="MeshNodeStreamExtensions.GetMeshNode"/>'s <c>MeshNode?</c> cannot carry.
///
/// <para>🚨 <b>"Not there" is three different facts, and a caller that acts on them
/// identically corrupts data.</b> A node can be absent because it never existed, absent
/// because its DELETE is in flight (the per-node hub's read tombstone answers <c>null</c>
/// <em>by design</em> — <c>MeshDataSource.AddReadValidatorPipeline</c>), or "absent" only
/// because the read itself failed. <c>InstanceSyncWorker.PullOne</c> collapsed all three into
/// one <c>null</c> and read it as "create it": it re-applied a node a user had just deleted
/// (Systemorph/MeshWeaver#1471), and, because a failed read is indistinguishable from an empty
/// one, would equally have clobbered a live local node with an older remote copy.</para>
///
/// <para>Consume it with a <c>switch</c> expression, and make the fall-through arm the arm that
/// does NOT write. C# cannot prove an enum switch exhaustive (an undeclared value is always
/// possible, hence CS8509), so the compiler will not sweep call sites for you when a state is
/// added here — what this type buys is that every consumer must NAME the states it acts on, so a
/// new one cannot silently inherit "absent, therefore create".</para>
/// </summary>
public enum NodeReadStatus
{
    /// <summary>The node was read; <see cref="NodeReadOutcome.Node"/> holds it.</summary>
    Present,

    /// <summary>
    /// The node is genuinely not there: routing answered <see cref="Messaging.ErrorType.NotFound"/>,
    /// the owner answered with no data, or a read validator hid it (a filtered node is INVISIBLE
    /// to the reader by contract — that is what hiding means).
    /// </summary>
    Absent,

    /// <summary>
    /// The node exists but its DELETE is in flight — the owning hub's read tombstone
    /// (<c>RecentlyDeletedRegistry</c>) answered instead of the node. TRANSIENT and directional:
    /// the next authoritative answer is "gone", never "here again". A caller must NOT re-create,
    /// re-apply or re-persist the path on this — that is a resurrection of what a user just
    /// deleted.
    /// </summary>
    DeleteInProgress,

    /// <summary>
    /// The read could not be completed: the owner faulted, the delivery failed for a reason other
    /// than not-found, the payload could not be materialised, or the budget elapsed under
    /// <see cref="ReadTimeoutBehavior.EmitNull"/>. <see cref="NodeReadOutcome.Failure"/> carries
    /// the cause. This is NOT evidence about whether the node exists.
    /// </summary>
    Unavailable,
}

/// <summary>
/// The outcome of a one-shot node read — <see cref="Status"/> plus whatever that status carries.
/// Produced by <see cref="MeshNodeStreamExtensions.GetMeshNodeOutcome"/>;
/// <see cref="MeshNodeStreamExtensions.GetMeshNode"/> is that same read with the distinction
/// discarded (its documented <c>MeshNode?</c> contract), so the two can never drift apart.
/// </summary>
public sealed record NodeReadOutcome
{
    /// <summary>What the read established.</summary>
    public required NodeReadStatus Status { get; init; }

    /// <summary>The node — non-null exactly when <see cref="Status"/> is
    /// <see cref="NodeReadStatus.Present"/>.</summary>
    public MeshNode? Node { get; init; }

    /// <summary>Why the read could not be completed; set only for
    /// <see cref="NodeReadStatus.Unavailable"/>.</summary>
    public Exception? Failure { get; init; }

    /// <summary>The node was read.</summary>
    /// <param name="node">The node that was read.</param>
    /// <returns>A <see cref="NodeReadStatus.Present"/> outcome.</returns>
    public static NodeReadOutcome Present(MeshNode node) =>
        new() { Status = NodeReadStatus.Present, Node = node };

    // Immutable singletons for the two states that carry no payload — a constant, never a cache
    // (NoStaticState.md: "if it never takes a write after construction it's a constant").
    /// <summary>The node is genuinely not there.</summary>
    public static NodeReadOutcome Absent { get; } = new() { Status = NodeReadStatus.Absent };

    /// <summary>The node's delete is in flight; the owner answered with its read tombstone.</summary>
    public static NodeReadOutcome DeleteInProgress { get; } =
        new() { Status = NodeReadStatus.DeleteInProgress };

    /// <summary>The read could not be completed — this says nothing about whether the node exists.</summary>
    /// <param name="failure">The cause, when one is available.</param>
    /// <returns>An <see cref="NodeReadStatus.Unavailable"/> outcome.</returns>
    public static NodeReadOutcome Unavailable(Exception? failure) =>
        new() { Status = NodeReadStatus.Unavailable, Failure = failure };
}
