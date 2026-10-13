using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MeshWeaver.Mesh;

namespace MeshWeaver.Data;

/// <summary>
/// 🚨 Keeps the cross-hub MeshNode writes that reach a node's store before it has loaded in the
/// order they reached it (#6594).
///
/// <para><b>The defect.</b> <c>ApplyMeshNodePatchInTurn</c> runs its merge on the primary store's
/// turn. When that turn finds the store still empty (the cold-activation window), the write used to
/// subscribe for the load and, when it arrived, post a fresh merge turn — to the BACK of the
/// primary's queue. Every write that had been queued meanwhile ran first, against the loaded store,
/// and the delayed write then committed on top of them: a sender's writes landed out of order and
/// the last one posted did not stay.</para>
///
/// <para><b>The rule.</b> While a write is waiting for the store, every later MeshNode write on
/// that store queues behind it, in the order its merge turn ran. When the store loads, the head
/// runs, and each finished write dispatches the next. Ordering is per node, which includes the
/// per-sender order #6589 established: nothing a queued write waits for depends on a reply from
/// another write, so holding them behind one another cannot deadlock.</para>
///
/// <para><b>Threading.</b> Every member is called from a merge turn of the primary store this lane
/// belongs to, and those turns are serial, so the queue needs no lock. A path that is not on such a
/// turn (the head's load bound expiring) posts a turn to act on the lane.</para>
/// </summary>
internal sealed class ColdStoreWriteLane
{
    /// <summary>One queued write.</summary>
    /// <param name="Dispatch">Posts that write's merge turn again, as a write released from the lane.</param>
    /// <param name="Fail">Answers that write with a refusal, without running it.</param>
    internal sealed record Entry(Action Dispatch, Action<MeshNodeError> Fail);

    private ImmutableQueue<Entry> queue = ImmutableQueue<Entry>.Empty;

    /// <summary>True while a write waits for the store, so a later write must queue behind it.</summary>
    public bool IsHeld => !queue.IsEmpty;

    /// <summary>Appends a write. The first one appended to an empty lane is its head.</summary>
    public void Enqueue(Entry entry) => queue = queue.Enqueue(entry);

    /// <summary>
    /// The head has been answered: removes it and dispatches the next write, which becomes the head.
    /// </summary>
    public void ReleaseHead()
    {
        if (queue.IsEmpty)
            return;
        queue = queue.Dequeue();
        if (!queue.IsEmpty)
            queue.Peek().Dispatch();
    }

    /// <summary>
    /// The store never loaded for the head: every queued write meets the same unloaded store, so
    /// each is answered with <paramref name="error"/> — or, with <c>null</c>, left to the owner's
    /// disposal NACK, which answers an in-flight write when the owner is going away.
    /// </summary>
    public void FailAll(MeshNodeError? error)
    {
        var drained = queue;
        queue = ImmutableQueue<Entry>.Empty;
        if (error is null)
            return;
        foreach (var entry in drained)
            entry.Fail(error);
    }
}

/// <summary>
/// The hub's <see cref="ColdStoreWriteLane"/>s, one per primary store. Registered scoped, so one
/// instance per hub and its lifetime is the hub's (never static — NoStaticState).
/// </summary>
internal sealed class ColdStoreWriteLanes
{
    private readonly ConditionalWeakTable<object, ColdStoreWriteLane> lanes = new();

    /// <summary>The lane for <paramref name="primary"/>, created on first use.</summary>
    public ColdStoreWriteLane For(object primary) => lanes.GetValue(primary, _ => new ColdStoreWriteLane());
}
