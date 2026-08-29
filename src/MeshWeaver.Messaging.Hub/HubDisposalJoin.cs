using System.Reactive;

namespace MeshWeaver.Messaging;

/// <summary>
/// Dispose a hub and JOIN its teardown — the one shape a test base, an xUnit fixture or a
/// run harness may use, because all three go on to do something the hub's still-running
/// teardown cannot survive.
///
/// <para><b>Why a bare <c>Dispose()</c> is a crash and not a leak.</b>
/// <see cref="System.IDisposable.Dispose"/> on a hub STARTS the disposal state machine and returns
/// immediately (<c>MessageHub.Dispose</c> says so in its own remarks); the action blocks drain, the
/// hosted hubs tear down, the sync streams unregister and the registrants run — all afterwards, on
/// other threads. A caller that disposes and then proceeds is running CONCURRENTLY with that. In a
/// harness the very next thing is one of: disposing the service scope (a continuation resolves from
/// a dead Autofac scope), unloading a collectible node ALC (a live thread dereferences freed
/// metadata — a native use-after-unload <b>SIGSEGV</b>, exit 139), or returning to xUnit so the next
/// test class starts on top of the previous one's live teardown. The observed signature is a burst
/// of <c>[SYNC_STREAM] Not setting … — stream is disposed</c> and
/// <c>resubscribe failed … TargetInvocationException</c> and then the process dies mid-run with no
/// failing test named (MeshWeaver.Plugins run 33236823482, job 99060770662).</para>
///
/// <para><b>So the rule is:</b> in a teardown or run-boundary path, every hub disposal is followed
/// by a JOIN on that hub's <see cref="IMessageHub.DisposalCompleted"/> before the caller proceeds.
/// <c>MeshWeaver.Mesh.MeshTeardownExtensions</c> is the richer form for a MESH ROOT (it also
/// cancels+joins the <c>IIoPool</c> leaves and quiesces the <c>AsyncDisposeQueue</c>); this type is
/// the small form for every OTHER hub a harness owns — the per-test client hubs, a silo-side hosted
/// hub, the gate runner's render client.</para>
///
/// <para><b>Bounded, and LOUD when the bound is hit.</b> A silent hang is not an improvement over a
/// crash: both waits take a budget, and expiry is REPORTED through the caller's sink rather than
/// swallowed. The budget exists to keep a wedged action block from hanging a whole suite — it is not
/// a number to raise when it fires. A join that times out is a hub that is not finishing; find it.</para>
///
/// <para>🚨 <b>No <c>Timeout</c> is composed into the signal</b>, in either form. Both go through
/// <see cref="ReactiveCompletion.ObserveCompletion"/> — bounded by a
/// <see cref="CancellationTokenSource"/> (async) or by <c>Task.Wait(TimeSpan)</c> (sync) — so the
/// subscription stays attached after the wait gives up and a fault arriving later is still reported
/// instead of becoming the unobserved exception that xUnit v3 escalates to a Catastrophic failure
/// (#2301/#2488). It is also what makes the blocking form safe: the task is completed with
/// <c>RunContinuationsAsynchronously</c>, so the signalling thread never carries on into the
/// caller's code.</para>
/// </summary>
public static class HubDisposalJoin
{
    /// <summary>
    /// The budget both joins default to, matching the dispose deadline the monolith test base
    /// already enforces (<c>MonolithMeshTestBase.DisposeTimeout</c>).
    /// </summary>
    public static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Disposes <paramref name="hub"/> and awaits its <see cref="IMessageHub.DisposalCompleted"/>.
    /// The form for an <c>async ValueTask DisposeAsync()</c> / <c>IAsyncLifetime</c> teardown: it
    /// SUSPENDS the caller rather than parking its thread, so it cannot self-deadlock against the
    /// hub's own scheduler.
    /// </summary>
    /// <param name="hub">The hub to dispose and join. A <c>null</c> is a no-op.</param>
    /// <param name="report">
    /// Where a dispose failure, a timeout or a late fault is SAID. Never <c>null</c> and never an
    /// empty lambda — a discarded teardown fault is half of what this method exists to remove.
    /// </param>
    /// <param name="timeout">The join budget; <see cref="DefaultJoinTimeout"/> when omitted.</param>
    public static async Task DisposeAndJoinAsync(
        this IMessageHub? hub, Action<string> report, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (hub is null) return;
        report = Safely(report);
        var budget = timeout ?? DefaultJoinTimeout;
        var address = Describe(hub);

        if (!TryDispose(hub, address, report)) return;

        await hub.DisposalCompleted
            .JoinDisposalAsync(address, report, budget, hub.GetPendingRequestDiagnostics)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits a disposal-completion signal that something ELSE already started — a hub's
    /// <see cref="IMessageHub.DisposalCompleted"/>, a
    /// <see cref="HostedHubsCollection.DisposalCompleted"/>, any composed teardown signal — with the
    /// same bound and the same loudness as <see cref="DisposeAndJoinAsync"/>. Split out so the join
    /// itself is exercisable against a bare subject, which is how this repo pins signal behaviour
    /// (see <c>DisposalWaitBridgeTest</c>'s remarks on why a real hub is the wrong instrument).
    /// </summary>
    /// <param name="disposalCompleted">The completion signal. Must terminate.</param>
    /// <param name="description">How the thing being joined is named in a report — an address.</param>
    /// <param name="report">Where a timeout, a fault or a late fault is SAID.</param>
    /// <param name="timeout">The join budget; <see cref="DefaultJoinTimeout"/> when omitted.</param>
    /// <param name="diagnostics">Optional in-flight snapshot, appended to a timeout report so it
    /// says WHY. Evaluated only on expiry, and never allowed to throw out of teardown.</param>
    /// <returns><c>true</c> if the signal terminated within the budget; <c>false</c> otherwise.</returns>
    public static async Task<bool> JoinDisposalAsync(
        this IObservable<Unit> disposalCompleted, string description, Action<string> report,
        TimeSpan? timeout = null, Func<string?>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(disposalCompleted);
        ArgumentNullException.ThrowIfNull(report);
        report = Safely(report);
        var budget = timeout ?? DefaultJoinTimeout;

        using var cts = new CancellationTokenSource(budget);
        try
        {
            // ConfigureAwait(false) on top of ObserveCompletion's RunContinuationsAsynchronously:
            // `await` captures TaskScheduler.Current absent a SynchronizationContext, so a teardown
            // entered from a hub scheduler would otherwise carry the rest of itself back onto one.
            await disposalCompleted
                .ObserveCompletion(LateFault(description, report), cts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            report(NotJoined(description, budget, diagnostics));
            return false;
        }
        catch (Exception ex)
        {
            report(Faulted(description, ex));
            return false;
        }
    }

    /// <summary>
    /// Disposes <paramref name="hub"/> and BLOCK-JOINS its
    /// <see cref="IMessageHub.DisposalCompleted"/>. The form for a genuinely synchronous run
    /// boundary — a harness's <see cref="System.IDisposable.Dispose"/>, a tool's exit path — where
    /// there is no <c>await</c>-able caller to suspend. This is the sanctioned exception to the
    /// no-blocking rule and nothing else: never call it from hub- or view-reachable code, where
    /// parking the calling thread on work that thread may itself have to run IS the deadlock.
    /// </summary>
    /// <param name="hub">The hub to dispose and join. A <c>null</c> is a no-op (returns <c>true</c>).</param>
    /// <param name="report">Where a dispose failure, a timeout or a late fault is SAID.</param>
    /// <param name="timeout">The join budget; <see cref="DefaultJoinTimeout"/> when omitted.</param>
    /// <returns><c>true</c> if teardown finished within the budget; <c>false</c> if it did not —
    /// the caller has been told through <paramref name="report"/> either way.</returns>
    public static bool DisposeAndJoin(
        this IMessageHub? hub, Action<string> report, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (hub is null) return true;
        report = Safely(report);
        var budget = timeout ?? DefaultJoinTimeout;
        var address = Describe(hub);

        return TryDispose(hub, address, report)
               && hub.DisposalCompleted.JoinDisposal(
                   address, report, budget, hub.GetPendingRequestDiagnostics);
    }

    /// <summary>
    /// The block-joining twin of <see cref="JoinDisposalAsync"/>, for a signal something ELSE
    /// already started. Same bound, same three distinguishable outcomes, same loudness.
    /// </summary>
    /// <param name="disposalCompleted">The completion signal. Must terminate.</param>
    /// <param name="description">How the thing being joined is named in a report — an address.</param>
    /// <param name="report">Where a timeout, a fault or a late fault is SAID.</param>
    /// <param name="timeout">The join budget; <see cref="DefaultJoinTimeout"/> when omitted.</param>
    /// <param name="diagnostics">Optional in-flight snapshot, appended to a timeout report.</param>
    /// <returns><c>true</c> if the signal terminated within the budget; <c>false</c> otherwise.</returns>
    public static bool JoinDisposal(
        this IObservable<Unit> disposalCompleted, string description, Action<string> report,
        TimeSpan? timeout = null, Func<string?>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(disposalCompleted);
        ArgumentNullException.ThrowIfNull(report);
        report = Safely(report);
        var budget = timeout ?? DefaultJoinTimeout;

        // The SAME bridge the async overload uses, waited on synchronously. Going through
        // ReactiveCompletion rather than hand-rolling an event is what keeps the two forms honest
        // about the same outcomes, and it is what makes the block safe: the task is completed with
        // RunContinuationsAsynchronously, so the signalling thread returns to its own business
        // instead of running the rest of this method, and the error arm stays attached after the
        // wait, so a fault arriving later is still SAID.
        var joined = disposalCompleted.ObserveCompletion(LateFault(description, report));

        // 🚨 Task.Wait(TimeSpan), NOT .Result / .GetAwaiter().GetResult(): expiry comes back as
        // FALSE rather than as an exception, so the three outcomes stay distinguishable — joined,
        // faulted (the AggregateException below), budget expired. All three are reported and none
        // escapes a teardown path as an exception nobody catches.
        bool completed;
        try
        {
            completed = joined.Wait(budget);
        }
        catch (AggregateException ex)
        {
            // A disposal FAULT. It settled the wait, so it never reached the late-fault arm and
            // would otherwise be lost with the task.
            report(Faulted(description, ex.GetBaseException()));
            return false;
        }

        if (completed) return true;

        report(NotJoined(description, budget, diagnostics));
        return false;
    }

    /// <summary>
    /// Wraps the caller's sink so it can never be the thing that fails a teardown. Reporters here
    /// are usually loggers or an xUnit output helper, and BOTH throw once their scope is gone — an
    /// <c>ITestOutputHelper</c> after the test has been reported, an <c>ILogger</c> whose provider
    /// went down with the service scope. Propagating that would raise an exception on whatever
    /// thread signalled disposal, which is the failure this whole type exists to prevent. The
    /// information is kept on <see cref="System.Diagnostics.Trace"/> instead of being lost, exactly
    /// as <see cref="ReactiveCompletion"/> does for its own late-fault reporter.
    /// </summary>
    private static Action<string> Safely(Action<string> report) =>
        message =>
        {
            try { report(message); }
            catch (Exception reporterFailure)
            {
                System.Diagnostics.Trace.TraceError(
                    "HubDisposalJoin: the report sink threw ({0}: {1}) while reporting: {2}",
                    reporterFailure.GetType().Name, reporterFailure.Message, message);
            }
        };

    private static bool TryDispose(IMessageHub hub, string address, Action<string> report)
    {
        try
        {
            hub.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            // Nothing was started, so nothing can be joined — say so and let the caller continue.
            report($"[dispose-join] hub {address}: Dispose() threw {ex.GetType().Name}: {ex.Message} "
                   + "— teardown was NOT joined.");
            return false;
        }
    }

    private static Action<Exception> LateFault(string description, Action<string> report) =>
        error => report(
            $"[dispose-join] hub {description}: disposal faulted AFTER the join stopped waiting on "
            + $"it — {error.GetType().Name}: {error.Message}. Reported rather than orphaned.");

    private static string Faulted(string description, Exception error) =>
        $"[dispose-join] hub {description}: disposal FAULTED — "
        + $"{error.GetType().Name}: {error.Message}";

    private static string NotJoined(string description, TimeSpan budget, Func<string?>? diagnostics)
    {
        var detail = SafeDiagnostics(diagnostics);
        return $"[dispose-join] hub {description}: teardown did NOT complete within "
               + $"{budget.TotalSeconds:F0}s. The caller is proceeding OVER live teardown — that is "
               + "the use-after-dispose that crashes the host, so find what on this hub's action "
               + "block is not finishing; do not widen the budget."
               + (detail is null ? string.Empty : "\n" + detail);
    }

    /// <summary>
    /// The caller's in-flight snapshot, so a timeout says WHY. Defensive twice over: the snapshot is
    /// taken from a hub that is mid-teardown, and reporting must never be the thing that throws out
    /// of a teardown path.
    /// </summary>
    private static string? SafeDiagnostics(Func<string?>? diagnostics)
    {
        if (diagnostics is null) return null;
        try { return diagnostics(); }
        catch (Exception ex) { return $"(diagnostics unavailable: {ex.GetType().Name})"; }
    }

    private static string Describe(IMessageHub hub)
    {
        try { return hub.Address.ToString() ?? "<unknown>"; }
        catch (Exception ex) { return $"<address unavailable: {ex.GetType().Name}>"; }
    }
}
