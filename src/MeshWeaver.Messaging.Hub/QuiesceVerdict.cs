namespace MeshWeaver.Messaging;

/// <summary>
/// The decision a hub takes when its Quiescing wait ends — the poll saw the response callbacks
/// drain, or the budget elapsed. Pure, so the decision table is pinned by a test without racing a
/// clock against a poll.
///
/// <para>🚨 <b>What is pending AT THE VERDICT decides, not which of the two signals fired first.</b>
/// The drain is sampled every poll interval and the deadline is its own timer, so a reply that
/// lands after the last sample and before the verdict reaches the hub as "deadline first" with
/// nothing pending. Treating that as a timeout set the sticky <c>QuiescingTimedOut</c> flag over a
/// callback that had been answered, and every test base fails its class on that flag — a red run
/// whose own report said nothing leaked. Nothing pending is <see cref="Outcome.Drained"/>.</para>
/// </summary>
public static class QuiesceVerdict
{
    /// <summary>What the hub does next.</summary>
    public enum Outcome
    {
        /// <summary>No callback is pending: advance, nothing to report.</summary>
        Drained,

        /// <summary>Pending callbacks are owed by hubs of this mesh that are themselves shutting
        /// down and answer before they go: wait another budget instead of cancelling.</summary>
        ReArm,

        /// <summary>Callbacks are still pending and nothing owes them: flag, report, cancel.</summary>
        TimedOut,
    }

    /// <summary>Decides the outcome of a Quiescing wait.</summary>
    /// <param name="pollDrained">Whether the poll observed the callbacks drain before the deadline.</param>
    /// <param name="pendingAtVerdict">Callbacks still registered when the verdict is taken.</param>
    /// <param name="owedByShuttingDownSiblings">How many of those a shutting-down hub of this mesh owes.</param>
    /// <param name="rearms">Budgets already re-armed in this wait.</param>
    /// <param name="maxRearms">The cycle breaker on re-arms.</param>
    /// <returns>The outcome.</returns>
    public static Outcome Of(
        bool pollDrained, int pendingAtVerdict, int owedByShuttingDownSiblings, int rearms, int maxRearms)
    {
        if (pollDrained || pendingAtVerdict == 0)
            return Outcome.Drained;
        return owedByShuttingDownSiblings > 0 && rearms < maxRearms
            ? Outcome.ReArm
            : Outcome.TimedOut;
    }
}
