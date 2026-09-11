using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.Fixture;

/// <summary>What waiting for a mesh's retired collectible contexts ended with.</summary>
/// <param name="Rounds">Collection rounds driven (0 when nothing was pending).</param>
/// <param name="RetainedContexts">Contexts that a full collection could not free — rooted by
/// something live. Empty unless <see cref="Retained"/>.</param>
/// <param name="Fault">The fault of an unload that was abandoned, when one was.</param>
public sealed record CollectibleUnloadOutcome(
    int Rounds, IReadOnlyList<string> RetainedContexts, Exception? Fault)
{
    /// <summary>Every retired context was collected.</summary>
    public bool Collected => Fault is null && RetainedContexts.Count == 0;

    /// <summary>Some retired context is still referenced after full collections freed nothing.</summary>
    public bool Retained => RetainedContexts.Count > 0;

    /// <inheritdoc />
    public override string ToString() =>
        Fault is not null
            ? $"unload FAULTED after {Rounds} round(s): {Fault.GetType().Name}: {Fault.Message}"
            : Retained
                ? $"{RetainedContexts.Count} context(s) RETAINED after {Rounds} round(s) freed nothing: "
                  + string.Join(", ", RetainedContexts)
                : $"all retired contexts collected after {Rounds} round(s)";
}

/// <summary>
/// Teardown's last step (Plugins#1605): wait until every collectible load context the mesh retired
/// has REALLY been unloaded, so the next mesh never starts while the previous one's contexts are
/// still being torn down.
///
/// <para>The wait itself is the reactive <see cref="CollectibleContextUnloads.AllCollected"/>
/// signal. What this adds is the one thing that signal cannot do for itself: a collectible context
/// is freed only by collections, and a test host waiting on it allocates nothing, so nothing would
/// ever collect. Each round is therefore one full blocking collection, a finalizer pass (where the
/// runtime destroys dead LoaderAllocators and releases their contexts), a second collection that
/// finds the released contexts, and a second finalizer pass that runs their sentinels.</para>
///
/// <para>🚨 There is no timer here. The loop ends on the signal, on a fault, or on a round that frees
/// nothing: once full collections stop shrinking <see cref="CollectibleContextUnloads.Pending"/>,
/// what remains is rooted by something live, and more collections cannot free it. That is reported
/// as RETAINED, never waited on. <see cref="NoProgressRounds"/> consecutive rounds are required
/// because one unload spans several finalizer passes (a LoaderAllocator's scout, then the
/// LCG resolvers that re-register for finalization, then the context itself).</para>
/// </summary>
public static class CollectibleUnloadDrain
{
    /// <summary>Consecutive rounds that free nothing before what is left counts as retained.</summary>
    public const int NoProgressRounds = 3;

    /// <summary>
    /// Drives collections until every context retired on <paramref name="unloads"/> is collected,
    /// one has faulted, or the rest are shown to be retained; then observes the completion signal.
    /// </summary>
    public static async ValueTask<CollectibleUnloadOutcome> WaitUntilCollectedAsync(
        CollectibleContextUnloads? unloads)
    {
        if (unloads is null || unloads.Pending == 0)
            return new CollectibleUnloadOutcome(0, [], null);

        // 🚨 Leave the caller's frame BEFORE collecting (measured, Plugins#1605). The drain is called
        // from a teardown that has just disposed the mesh, and that frame's stack slots still hold it:
        // a heap dump taken INSIDE the drain found the only non-weak root of every still-unloading
        // context in MonolithMeshTestBase.DisposeAsync's MoveNext frame — the disposed MessageHub →
        // Workspace → MeshDataSource → MeshContentTypeRegistry → DiscriminatorClaim → the collectible
        // RuntimeType. Collecting while that frame is live measures the teardown's own reference, not
        // the unload. After the yield the caller has returned to its awaiter and its frame is gone.
        await Task.Yield();

        var rounds = 0;
        var stalled = 0;
        var before = unloads.Pending;
        while (unloads.Pending > 0 && unloads.Faults.Count == 0 && stalled < NoProgressRounds)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            rounds++;
            var now = unloads.Pending;
            stalled = now < before ? 0 : stalled + 1;
            before = now;
        }

        if (unloads.Faults.Count > 0)
            return new CollectibleUnloadOutcome(rounds, [], unloads.Faults[0]);
        if (unloads.Pending > 0)
            return new CollectibleUnloadOutcome(rounds, unloads.PendingContextNames, null);

        // Every sentinel has run; its release is queued on the pool. Observe it — the signal, not
        // the count, is what says the unload finished. Through the sanctioned bridge: awaiting the
        // observable directly would resume this teardown inline on the pool thread that signalled.
        try
        {
            await unloads.AllCollected.Await();
            return new CollectibleUnloadOutcome(rounds, [], null);
        }
        catch (Exception ex)
        {
            return new CollectibleUnloadOutcome(rounds, [], ex);
        }
    }
}
