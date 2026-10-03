using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Content of a durable stream's STREAM node (<see cref="DurableStreamPaths.StreamPath"/>, NodeType
/// <see cref="DurableStreamNodeTypes.Stream"/>). Written ONLY by the stream node's own hub, which
/// serialises every append, claim, acknowledgement and release — so ordering and ownership are
/// decided in one place.
///
/// <para>The lease IS the subscription: <see cref="Subscriber"/> is the address of the one hub
/// currently consuming the stream, <see cref="LeaseEpoch"/> fences every earlier holder (an
/// acknowledgement carrying an older epoch is refused), and <see cref="Checkpoint"/> is the last
/// sequence that subscriber ACKNOWLEDGED — a new subscriber taking over resumes strictly after it.
/// A stream whose subscriber left is ORPHANED: <see cref="Subscriber"/> is null and
/// <see cref="OrphanedAt"/> says since when — that change of this node is the orphan event.</para>
/// </summary>
public sealed record DurableStreamState
{
    /// <summary>The stream family.</summary>
    public string Namespace { get; init; } = "";

    /// <summary>The owning node's path.</summary>
    public string Key { get; init; } = "";

    /// <summary>The sequence of the last item whose node was created (0 = empty).</summary>
    public long LastSequence { get; init; }

    /// <summary>The address of the subscriber holding the lease, or null when none does.</summary>
    public string? Subscriber { get; init; }

    /// <summary>Incremented on every grant; an acknowledgement must carry the current value.</summary>
    public long LeaseEpoch { get; init; }

    /// <summary>When the current lease was granted (UTC instant).</summary>
    public DateTimeOffset? SubscribedAt { get; init; }

    /// <summary>The last sequence acknowledged by a lease holder (0 = none).</summary>
    public long Checkpoint { get; init; }

    /// <summary>When <see cref="Checkpoint"/> last advanced (UTC instant).</summary>
    public DateTimeOffset? CheckpointAt { get; init; }

    /// <summary>When the stream lost its last subscriber (UTC instant); null while one holds it.</summary>
    public DateTimeOffset? OrphanedAt { get; init; }

    /// <summary>The address of the subscriber that held the lease before it was orphaned.</summary>
    public string? LastSubscriber { get; init; }
}

/// <summary>
/// Content of ONE item node (<see cref="DurableStreamPaths.ItemsNamespace"/> /
/// <see cref="DurableStreamPaths.ItemId"/>, NodeType <see cref="DurableStreamNodeTypes.Item"/>).
/// Write-once: created by the stream's hub when the item is appended, never updated.
/// </summary>
public sealed record DurableStreamItemContent
{
    /// <summary>The item's sequence (equals the number in its node id).</summary>
    public long Sequence { get; init; }

    /// <summary>The payload as the producer serialized it (its runtime type, with the producing hub's
    /// options). Kept as text: a polymorphic payload needs its <c>$type</c> first, which a JSON
    /// document store would not preserve.</summary>
    public string PayloadJson { get; init; } = "";

    /// <summary>When the item was appended (UTC instant).</summary>
    public DateTimeOffset AppendedAt { get; init; }
}

/// <summary>The NodeTypes of a durable stream's nodes.</summary>
public static class DurableStreamNodeTypes
{
    /// <summary>The stream node.</summary>
    public const string Stream = "DurableStream";

    /// <summary>An item node.</summary>
    public const string Item = "DurableStreamItem";
}

/// <summary>Appends one item. Sent to the STREAM node's hub, which assigns the sequence.</summary>
/// <param name="PayloadJson">The serialized payload.</param>
public sealed record AppendDurableStreamItemRequest(string PayloadJson) : IRequest<AppendDurableStreamItemResponse>;

/// <summary>The item is durable under <see cref="Sequence"/>, or <see cref="Error"/> says why not.</summary>
/// <param name="Sequence">The assigned sequence (0 on failure).</param>
/// <param name="Error">Null on success.</param>
public sealed record AppendDurableStreamItemResponse(long Sequence, string? Error);

/// <summary>
/// Asks the stream's hub for the lease on behalf of <paramref name="Subscriber"/> — the address of
/// the consuming hub, which the stream's hub probes for a sign of life when another claimant
/// arrives. Requests are issued from the mesh's read-issuing hub, not from the subscriber itself, so
/// a subscriber that is quiescing can still acknowledge and release; the lease EPOCH is the fencing
/// token every later acknowledgement must carry.
/// </summary>
/// <param name="Subscriber">The claiming hub's address.</param>
public sealed record ClaimDurableStreamRequest(string Subscriber) : IRequest<ClaimDurableStreamResponse>;

/// <summary>The stream hub's verdict on a claim.</summary>
/// <param name="Granted">True when the sender now holds the lease.</param>
/// <param name="Epoch">The granted lease epoch (carry it on every acknowledgement).</param>
/// <param name="Checkpoint">The last acknowledged sequence — resume strictly after it.</param>
/// <param name="Holder">On refusal: the live holder.</param>
/// <param name="TookOverFrom">On a grant that replaced an unreachable holder: that holder.</param>
public sealed record ClaimDurableStreamResponse(bool Granted, long Epoch, long Checkpoint, string? Holder, string? TookOverFrom);

/// <summary>Acknowledges that <paramref name="Subscriber"/> processed every item up to <paramref name="Sequence"/>.</summary>
/// <param name="Subscriber">The subscriber's address.</param>
/// <param name="Epoch">The lease epoch the subscriber was granted.</param>
/// <param name="Sequence">The last processed sequence.</param>
public sealed record AckDurableStreamRequest(string Subscriber, long Epoch, long Sequence) : IRequest<AckDurableStreamResponse>;

/// <summary>Whether the acknowledgement advanced the checkpoint, or why it was refused.</summary>
/// <param name="Accepted">True when recorded.</param>
/// <param name="Reason">On refusal: the sender no longer holds the lease (fenced), and who does.</param>
public sealed record AckDurableStreamResponse(bool Accepted, string? Reason);

/// <summary><paramref name="Subscriber"/> gives up the lease it holds under <paramref name="Epoch"/>; the stream is orphaned.</summary>
/// <param name="Subscriber">The subscriber's address.</param>
/// <param name="Epoch">The lease epoch being released.</param>
public sealed record ReleaseDurableStreamRequest(string Subscriber, long Epoch) : IRequest<ReleaseDurableStreamResponse>;

/// <summary>The release was applied (or was stale and ignored).</summary>
/// <param name="Released">True when the stream is now orphaned by this release.</param>
public sealed record ReleaseDurableStreamResponse(bool Released);

/// <summary>
/// "An item of this stream is waiting and nobody holds its lease": posted by the stream's hub to
/// the stream's OWNER (<see cref="DurableStreamId.Key"/>) after an append to an orphaned stream.
/// Delivering it activates the owner's hub, whose initialization starts its consumer. Handled as a
/// no-op by every consuming hub.
/// </summary>
/// <param name="Namespace">The stream family.</param>
/// <param name="Key">The owner's path.</param>
public sealed record DurableStreamWake(string Namespace, string Key);
