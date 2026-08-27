namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE sanctioned bridge from a reactive completion signal — a hub's
/// <see cref="IMessageHub.DisposalCompleted"/>, a drain report, a grain's deactivation — to a
/// <see cref="Task"/>, for the signatures that are not ours to change (an
/// <c>ILifecycleObserver.OnStop</c>, an <c>IHostedService</c>, an <c>async Task</c> test method).
/// Everywhere else, waiting means <c>signal.Subscribe(onNext, onError)</c> and nothing else —
/// see the <c>/async</c> skill, Rule 1 and Rule 1a.
///
/// <para>🚨 <b>Why <c>.ToTask()</c> is dangerous: it DEADLOCKS. That is the primary failure, not a
/// lost exception.</b> `await`ing a completion parks the awaiting flow until the observable
/// produces — and mesh completions are produced BY THE VERY SCHEDULER the awaiting flow is
/// occupying. A hub's disposal finishes when its single-threaded action block drains; a grain's
/// deactivation finishes when its turn scheduler is free. Wait on one of those from that same
/// scheduler and the thing you are waiting for can never happen: the Task never settles, and the
/// only thing that ever ends the wait is whatever timeout was raced against it.</para>
///
/// <para><b>The trap is that you do not have to WRITE the block to get it</b> — Rx hands it to
/// you. <c>ToTask()</c> completes its <c>TaskCompletionSource</c> from INSIDE the Rx pipeline,
/// without <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so
/// <c>TrySetResult</c> resumes the awaiter INLINE on the signalling thread. Everything after that
/// <c>await</c> — the rest of the method — then runs on the hub's disposal thread or the grain's
/// turn scheduler. Worse, it is sticky: <c>await</c> captures
/// <see cref="TaskScheduler"/>.<see cref="TaskScheduler.Current"/> when there is no
/// <see cref="System.Threading.SynchronizationContext"/>, so once one continuation lands on that
/// scheduler, EVERY later await in the same method schedules onto it too. That is issue #2301:
/// <c>OrleansGrainTeardownStragglerTest</c> resumed inline on the deactivating grain's scheduler
/// and then held it, waiting for that grain's activation to leave the catalog — which needed the
/// scheduler it was holding. It failed at exactly 30 s, its <c>Timeout</c> budget, every time; a
/// healthy activation leaves the catalog in 0.10 s, and a number that is always the budget rather
/// than a distribution around it is the signature of a deadlock, not of contention.</para>
///
/// <para><b>The unobserved-fault problem is real but SECONDARY.</b> When the timeout finally fires
/// it settles the Task, and anything still travelling the chain has no observer left — an
/// unobserved exception, surfaced on the finalizer as
/// <see cref="TaskScheduler.UnobservedTaskException"/>, which xUnit v3 escalates to a Catastrophic
/// failure that poisons the NEXT test class (the <c>HOST_CRASHED</c> marker on #2301). It is what
/// the deadlock does on its way out.</para>
///
/// <para><b>What this method guarantees.</b> It never blocks and it never resumes its caller on the
/// signalling thread: the wait is a <c>Subscribe</c>, and the task is completed through a
/// <see cref="TaskCompletionSource{TResult}"/> created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> — which is load-bearing, not
/// tidiness. It is the line that stops the caller's continuation from running on the hub or grain
/// scheduler that signalled, and therefore the line that stops the deadlock. Additionally, the
/// subscription is NOT torn down when the task settles, so a fault arriving afterwards still has
/// an error arm to reach.</para>
///
/// <para>🚨 <b>What this is NOT.</b> Not a general-purpose <c>FirstAsync</c>, and not for a hot
/// stream that never terminates (the subscription would live forever by design). It is for
/// COMPLETION SIGNALS: sources that fire once and then complete or fault — the contract every
/// signal named above already has.</para>
///
/// <para><b>And no <c>Timeout</c>.</b> A bound composed INTO the wait races the thing being waited
/// for and settles the waiter while that work is still in flight. A caller that needs a wall-clock
/// bound takes one from the edge it already lives at — xUnit's <c>[Fact(Timeout = …)]</c>, a host's
/// shutdown token, a <see cref="CancellationToken"/> passed here. A wait that does not finish is a
/// wedge to find (AGENTS.md: no band-aids), not a budget to spend.</para>
/// </summary>
public static class ReactiveCompletion
{
    /// <summary>
    /// Subscribes to <paramref name="source"/> with an error arm and returns a <see cref="Task"/>
    /// that settles on its FIRST notification — the value, its completion, or its fault — WITHOUT
    /// blocking and WITHOUT resuming the caller on the thread that signalled.
    ///
    /// <para>The subscription outlives the returned task on purpose: a fault arriving after the
    /// task has settled is reported to <paramref name="reportLateFault"/> rather than becoming an
    /// unobserved exception.</para>
    /// </summary>
    /// <typeparam name="T">The signal's payload type (typically <see cref="System.Reactive.Unit"/>).</typeparam>
    /// <param name="source">The completion signal. Must terminate (complete or fault) — see the
    /// type remarks; pointing this at a never-ending stream leaks the subscription.</param>
    /// <param name="reportLateFault">Receives a fault that arrives AFTER the task settled — the
    /// case a <see cref="Task"/> cannot represent. Log it, write it to test output, fail the
    /// class: anything except discarding it. Never <c>null</c>, and never an empty lambda: an
    /// ignored late fault is half of what this method exists to remove.</param>
    /// <param name="cancellationToken">Cancels the WAIT, not the source. The error arm stays
    /// attached afterwards, so a fault that lands after cancellation is still reported.</param>
    /// <returns>The first value, or <c>default</c> if the source completed without one.</returns>
    public static Task<T?> ObserveCompletion<T>(
        this IObservable<T> source,
        Action<Exception> reportLateFault,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reportLateFault);

        // 🚨 RunContinuationsAsynchronously IS THE DEADLOCK FIX. Without it — which is exactly
        // what Rx's own ToTask() does — TrySetResult below resumes the awaiting caller INLINE on
        // whichever thread signalled: the hub's disposal thread, the grain's turn scheduler. The
        // caller then does the rest of its work there, holding a scheduler that the work it is
        // about to wait for needs. With it, the continuation is queued instead, and the signalling
        // thread returns to its own business immediately. Pinned by
        // DisposalWaitBridgeTest.ObserveCompletion_NeverResumesItsCallerOnTheSignallingThread.
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<T?>)state!).TrySetCanceled(),
                completion);
            // Release the registration once the wait is over, whichever way it ended. Not a wait
            // of its own: the continuation only disposes, and the task itself is observed by the
            // caller that awaits it.
            completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // 🚨 The returned IDisposable is deliberately dropped. Unsubscribing on settle is what
        // .ToTask() does and what loses a late fault; the subscription instead releases itself
        // when the SOURCE terminates, which is the only moment after which no fault can still
        // arrive. The observer is rooted by the source until then.
        source.Subscribe(
            value => completion.TrySetResult(value),
            error =>
            {
                // Before the task settled this IS the answer; after it, the task can no longer
                // carry it — so it goes to the reporter rather than nowhere.
                if (completion.TrySetException(error))
                    return;
                try
                {
                    reportLateFault(error);
                }
                catch (Exception reporterFailure)
                {
                    // A reporter that throws would send BOTH exceptions up the producer's call
                    // stack — a fault with no observer, on whatever thread signalled disposal.
                    // Reporters are usually loggers, and a logger whose provider was disposed with
                    // the scope DOES throw. Fall back to the one sink that cannot: keep the
                    // information, never propagate.
                    System.Diagnostics.Trace.TraceError(
                        "ObserveCompletion: the late-fault reporter threw ({0}: {1}) while reporting {2}: {3}",
                        reporterFailure.GetType().Name, reporterFailure.Message,
                        error.GetType().Name, error.Message);
                }
            },
            () => completion.TrySetResult(default));

        return completion.Task;
    }
}
