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
/// <para>🚨 <b>No <c>Timeout</c> is composed into the signal</b>, in either overload — the bound is a
/// <see cref="CancellationTokenSource"/> (async) or a <see cref="ManualResetEventSlim"/> wait (sync),
/// and the subscription stays attached afterwards, so a fault arriving after the wait gave up is
/// still reported instead of becoming the unobserved exception that xUnit v3 escalates to a
/// Catastrophic failure (#2301/#2488, and <see cref="ReactiveCompletion"/>'s remarks).</para>
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

        using var cts = new CancellationTokenSource(budget);
        try
        {
            // ConfigureAwait(false) on top of ObserveCompletion's RunContinuationsAsynchronously:
            // `await` captures TaskScheduler.Current absent a SynchronizationContext, so a teardown
            // entered from a hub scheduler would otherwise carry the rest of itself back onto one.
            await hub.DisposalCompleted
                .ObserveCompletion(LateFault(address, report), cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            report(NotJoined(address, budget, hub));
        }
        catch (Exception ex)
        {
            report($"[dispose-join] hub {address}: disposal FAULTED — {ex.GetType().Name}: {ex.Message}");
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

        if (!TryDispose(hub, address, report)) return false;

        var joined = new ManualResetEventSlim(false);
        var lateFault = LateFault(address, report);
        var settled = 0;

        // 🚨 The subscription handle is deliberately dropped, exactly as ReactiveCompletion does:
        // unsubscribing when the WAIT ends is what loses a fault that arrives afterwards. The
        // observer is rooted by DisposalCompleted (a ReplaySubject) until the source terminates,
        // which is the only moment after which no fault can still come.
        hub.DisposalCompleted.Subscribe(
            _ => { },
            error =>
            {
                if (Interlocked.Exchange(ref settled, 1) == 0)
                {
                    report($"[dispose-join] hub {address}: disposal FAULTED — "
                           + $"{error.GetType().Name}: {error.Message}");
                    joined.Set();
                }
                else
                {
                    lateFault(error);
                }
            },
            () => { Interlocked.Exchange(ref settled, 1); joined.Set(); });

        if (joined.Wait(budget)) return true;

        Interlocked.Exchange(ref settled, 1);
        report(NotJoined(address, budget, hub));
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

    private static Action<Exception> LateFault(string address, Action<string> report) =>
        error => report(
            $"[dispose-join] hub {address}: disposal faulted AFTER the join stopped waiting on it "
            + $"— {error.GetType().Name}: {error.Message}. Reported rather than orphaned.");

    private static string NotJoined(string address, TimeSpan budget, IMessageHub hub)
    {
        var diagnostics = SafeDiagnostics(hub);
        return $"[dispose-join] hub {address}: teardown did NOT complete within "
               + $"{budget.TotalSeconds:F0}s. The caller is proceeding OVER live teardown — that is "
               + "the use-after-dispose that crashes the host, so find what on this hub's action "
               + "block is not finishing; do not widen the budget."
               + (diagnostics is null ? string.Empty : "\n" + diagnostics);
    }

    /// <summary>
    /// The hub's own in-flight snapshot, so a timeout says WHY. Defensive: the hub is mid-teardown
    /// and diagnostics must never be the thing that throws out of a teardown path.
    /// </summary>
    private static string? SafeDiagnostics(IMessageHub hub)
    {
        try { return hub.GetPendingRequestDiagnostics(); }
        catch (Exception ex) { return $"(diagnostics unavailable: {ex.GetType().Name})"; }
    }

    private static string Describe(IMessageHub hub)
    {
        try { return hub.Address.ToString() ?? "<unknown>"; }
        catch (Exception ex) { return $"<address unavailable: {ex.GetType().Name}>"; }
    }
}
