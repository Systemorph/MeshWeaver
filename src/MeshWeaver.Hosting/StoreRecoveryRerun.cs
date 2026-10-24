using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

/// <summary>
/// The bounded EARLY backoff a finite store-reading job (the boot warm-up sweep) adds to the shared
/// "the store answers again" evidence (<see cref="StoreReachability.StoreAnswersAgain"/>): how many
/// re-runs a timer may also trigger, and how long each waits. A host that registers none gets
/// <see cref="Default"/>.
/// </summary>
public sealed class StoreRecoveryRerunPolicy
{
    /// <summary>
    /// The production policy: up to <see cref="StandingWatchRecovery.DefaultMaxConsecutive"/> timed
    /// re-runs on <see cref="StandingWatchRecovery.DefaultBackoff"/> (2 s, 5 s, 15 s, 30 s, 60 s —
    /// about two minutes in all), the same schedule a standing watch re-opens on.
    /// </summary>
    public static StoreRecoveryRerunPolicy Default { get; } = new();

    /// <summary>
    /// Re-runs a TIMER may trigger. After that the job is re-run only on evidence that the store
    /// answers again, never on a clock.
    /// </summary>
    public int MaxTimedReruns { get; init; } = StandingWatchRecovery.DefaultMaxConsecutive;

    /// <summary>The delay before timed re-run number n (1-based).</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = StandingWatchRecovery.DefaultBackoff;
}

/// <summary>One scheduled re-run, as told to the caller BEFORE it waits, so the gap is never silent.</summary>
/// <param name="Fault">The transient fault that ended the previous attempt.</param>
/// <param name="Rerun">Which re-run this will be (1-based).</param>
/// <param name="MaxTimedReruns">How many re-runs a timer may trigger in all.</param>
/// <param name="Backoff">The timer's delay, or <see langword="null"/> once the timed re-runs are
/// spent and only evidence can trigger it.</param>
/// <param name="EvidenceAlreadyIn">The store was evidenced to answer again WHILE the faulted attempt
/// was in flight, so the re-run starts at once.</param>
/// <param name="CommitsCount">Whether a commit the store announced still counts as evidence for
/// this job (<c>false</c> once a commit-started re-run met the same fault again — then only a
/// reconnect, or the timer, starts one).</param>
public sealed record StoreRecoveryRerunScheduled(
    Exception Fault, int Rerun, int MaxTimedReruns, TimeSpan? Backoff,
    bool EvidenceAlreadyIn = false, bool CommitsCount = true)
{
    /// <summary>What will trigger the re-run, in one clause — for the log line and the health payload.</summary>
    public string Trigger
    {
        get
        {
            if (EvidenceAlreadyIn)
                return $"now: the store answered again while the attempt was in flight (re-run {Rerun})";
            var evidence = CommitsCount
                ? "when the store is evidenced to answer again (its change feed reconnects, or it announces a commit)"
                : "when the store's change feed reconnects (a commit no longer counts: a commit-started "
                  + "re-run met the same fault)";
            return Backoff is { } wait
                ? $"{evidence}, or in {wait.TotalSeconds:0.#}s (re-run {Rerun} of at most {MaxTimedReruns} timed)"
                : $"{evidence} (re-run {Rerun}; the {MaxTimedReruns} timed re-runs are spent, so no "
                  + "clock will trigger it)";
        }
    }
}

/// <summary>
/// 🚨 <b>A finite job that faulted on a TRANSIENT store fault is re-run once the store answers
/// again — on the same process</b> (MeshWeaver#7066).
///
/// <para><b>The defect this closes.</b> The boot warm-up sweep that feeds the <c>nodetype_bake</c>
/// readiness gate ran ONCE. On 2026-10-22 a managed-database restart refused connections for about
/// 40 seconds (#7068); one pod's sweep met that refusal on <c>Admin/Build</c> at 21:22:55Z, recorded
/// <see cref="BakePhase.Faulted"/>, and nothing ever ran it again. The database was back at about
/// 21:23:34Z; the pod refused readiness until it was replaced at 22:37Z, and the roll could not
/// converge in between. Under the readiness-only policy nothing restarts a refused pod, so "a
/// restart re-runs the sweep" was not a recovery path at all.</para>
///
/// <para><b>What triggers the re-run: the platform's ONE "the store answers again" evidence.</b>
/// That is <see cref="StoreReachability.StoreAnswersAgain"/> (#7072): a declared
/// <see cref="ChangeFeedGap"/> (the feed lost its connection and resumed, which a database restart
/// always produces) or a commit the store announced (<see cref="IMeshInvalidationFeed.DurableCommits"/>).
/// This class defines no signal of its own; it applies the same two evidence rules
/// <see cref="StoreReachability.ReissueWhenStoreAnswers"/> applies to a single write, to a job that
/// emits many results:</para>
/// <list type="bullet">
/// <item>Evidence is observed for the job's WHOLE life, and evidence that arrives while an attempt is
/// in flight is retained: a reconnect landing during the attempt that then faults starts the re-run at
/// once instead of being lost to a feed that never replays.</item>
/// <item>A re-run STARTED BY A COMMIT that meets the same fault shows a commit is not evidence for
/// this job (the store accepted someone's commit and still refused ours), so from then on only a
/// reconnect starts one. That keeps a busy mesh from becoming a re-run loop.</item>
/// </list>
///
/// <para><b>What this adds: a bounded early backoff</b> (<see cref="StoreRecoveryRerunPolicy"/>),
/// documented because it is the one clock here. The evidence does not cover every recovery: a feed
/// declares a gap only after it had come up once (a pod whose listener never connected declares
/// none), and a backend with no cross-process feed and no announced commits gives no evidence at
/// all. So for the first few re-runs only, a timer races the evidence; after those only evidence can
/// trigger a re-run and no clock runs. Every wait is told to the caller before it starts, so it is
/// named in the log and the health payload.</para>
///
/// <para><b>What it deliberately does NOT re-run.</b> Only <see cref="StandingWatchRecovery.IsTransient"/>
/// faults: a database connect/IO fault, the fan-in's stall terminal, or a store fault an owner
/// classified before answering across the hub boundary (<c>MeshNodeError.Transient</c>). A compile
/// error, a query or schema error, a deserialization defect — anything else — surfaces unchanged on
/// its first occurrence and stays a named failure; re-running a defect only repeats it.</para>
///
/// <para>Nothing polls, nothing parks a thread, and no state is static.</para>
/// </summary>
public static class StoreRecoveryRerun
{
    /// <summary>
    /// The store's "answers again" evidence for a mesh-wide job, with its SOURCE kept (<c>true</c>: the
    /// feed reconnected; <c>false</c>: the store announced a commit) — <see cref="StoreReachability"/>'s
    /// own evidence, counted over every partition, because the sweep reads every partition.
    /// </summary>
    /// <param name="feed">The mesh's invalidation feed, or <c>null</c> (then it never emits).</param>
    public static IObservable<bool> EvidenceFor(IMeshInvalidationFeed? feed)
        => StoreReachability.EvidenceBySource(feed, partition: null);

    /// <summary>
    /// Runs <paramref name="attempt"/>, and after a <see cref="StandingWatchRecovery.IsTransient"/>
    /// fault runs it again on the first piece of <paramref name="storeAnswers"/> evidence — retained
    /// if it arrived while the faulted attempt was in flight — or, for the first
    /// <see cref="StoreRecoveryRerunPolicy.MaxTimedReruns"/> re-runs, when the policy's backoff
    /// elapses, whichever comes first. Any other fault surfaces unchanged.
    /// </summary>
    /// <typeparam name="T">The job's emission.</typeparam>
    /// <param name="attempt">Cold factory for one attempt, given its re-run number (0 = the first
    /// run). Re-invoked per re-run, so it must build a FRESH upstream.</param>
    /// <param name="storeAnswers">The evidence, with its source (<see cref="EvidenceFor"/>); hot,
    /// observed for the life of the subscription.</param>
    /// <param name="policy">How many timed re-runs, and their backoff.</param>
    /// <param name="scheduler">Scheduler for the backoff timer.</param>
    /// <param name="onScheduled">Told each re-run BEFORE it waits — the caller records the fault and
    /// the pending re-run, so the gap between attempts is visible.</param>
    /// <param name="isTransient">The classification; <see cref="StandingWatchRecovery.IsTransient"/>
    /// when null.</param>
    /// <returns>Every attempt's emissions, completing when an attempt completes.</returns>
    public static IObservable<T> RerunOnStoreRecovery<T>(
        Func<int, IObservable<T>> attempt,
        IObservable<bool> storeAnswers,
        StoreRecoveryRerunPolicy policy,
        IScheduler scheduler,
        Action<StoreRecoveryRerunScheduled> onScheduled,
        Func<Exception, bool>? isTransient = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(storeAnswers);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(onScheduled);
        var classify = isTransient ?? StandingWatchRecovery.IsTransient;
        var timedBudget = Math.Max(0, policy.MaxTimedReruns);

        return Observable.Defer(() =>
        {
            // Per outer subscription, never shared. Evidence arrives on the feed's thread and faults on
            // the attempt's, so the hand-off between them is a plain lock around field updates — no
            // await or blocking wait happens inside it.
            var state = new RerunState();
            var evidence = storeAnswers.Subscribe(byGap =>
            {
                AsyncSubject<bool>? wake;
                lock (state)
                {
                    if (!byGap && !state.CommitsCount)
                        return;
                    if (state.Waiter is { } waiting)
                    {
                        state.Waiter = null;
                        wake = waiting;
                    }
                    else
                    {
                        // Retained for the attempt in flight; a reconnect outranks a commit.
                        state.Pending = state.Pending == true ? true : byGap;
                        return;
                    }
                }
                wake.OnNext(byGap);
                wake.OnCompleted();
            }, ex =>
            {
                // The evidence itself faulted: nothing can say any more that the store answers again,
                // so the job must not wait on it in silence. A waiting re-run surfaces the fault now;
                // otherwise the next transient fault does (no evidence, and no clock once the timed
                // re-runs are spent, would leave it waiting for ever).
                AsyncSubject<bool>? wake;
                lock (state)
                {
                    state.EvidenceFault = ex;
                    wake = state.Waiter;
                    state.Waiter = null;
                }
                wake?.OnError(ex);
            });

            var next = 0;
            // ONE flat pipeline re-subscribed in place by RetryWhen (StandingWatchRecovery's shape, for
            // the same reason: no Catch nesting one layer deeper per fault survived).
            return Observable.Defer(() =>
                {
                    lock (state)
                    {
                        // Whatever started this attempt is spent; only newer evidence counts for it.
                        state.Pending = null;
                        state.Waiter = null;
                    }
                    return attempt(next++);
                })
                .RetryWhen(faults => faults.SelectMany(fault =>
                {
                    if (!classify(fault))
                        return Observable.Throw<Unit>(fault);
                    var rerun = next;
                    TimeSpan? wait = rerun <= timedBudget ? policy.Backoff(rerun) : null;
                    bool? retained;
                    bool commitsCount;
                    Exception? evidenceFault;
                    AsyncSubject<bool>? waiter = null;
                    lock (state)
                    {
                        if (state.StartedByCommit)
                        {
                            // A commit started it and the store still refused it: commits are disproved.
                            state.CommitsCount = false;
                            if (state.Pending == false)
                                state.Pending = null;
                        }
                        retained = state.Pending;
                        state.Pending = null;
                        commitsCount = state.CommitsCount;
                        evidenceFault = retained is null ? state.EvidenceFault : null;
                        if (retained is null && evidenceFault is null)
                            state.Waiter = waiter = new AsyncSubject<bool>();
                    }

                    // RECORD FIRST, RE-RUN SECOND: the evidence wakes the waiter, which hands it over
                    // only once this selector has returned, so a re-run can never start (and mark
                    // itself running) before the fault it re-runs has been recorded.
                    onScheduled(new StoreRecoveryRerunScheduled(
                        fault, rerun, timedBudget, wait, retained is not null, commitsCount));

                    // With the evidence gone, only a pending timed re-run can still trigger one; once
                    // those are spent the evidence's own fault surfaces as the job's terminal.
                    IObservable<bool?> trigger = evidenceFault is { } broken
                        ? wait is { } lastDelay
                            ? Observable.Timer(lastDelay, scheduler).Select(_ => (bool?)null)
                            : Observable.Throw<bool?>(broken)
                        : retained is { } byGapNow
                        ? Observable.Return<bool?>(byGapNow)
                        : wait is { } delay
                            ? waiter!.Select(b => (bool?)b)
                                .Amb(Observable.Timer(delay, scheduler).Select(_ => (bool?)null))
                            : waiter!.Select(b => (bool?)b);
                    return trigger.Do(byGap =>
                    {
                        lock (state)
                            state.StartedByCommit = byGap == false;
                    }).Select(_ => Unit.Default);
                }))
                .Finally(evidence.Dispose);
        });
    }

    /// <summary>The state of one <see cref="RerunOnStoreRecovery{T}"/> subscription, owned by it alone.</summary>
    private sealed class RerunState
    {
        /// <summary>Evidence retained while an attempt was in flight: <c>true</c> a reconnect,
        /// <c>false</c> a commit, <c>null</c> none.</summary>
        public bool? Pending;

        /// <summary>The waiter of a re-run that is waiting for evidence, or <c>null</c>.</summary>
        public AsyncSubject<bool>? Waiter;

        /// <summary>Whether a commit still counts as evidence for this job.</summary>
        public bool CommitsCount = true;

        /// <summary>Whether the attempt in flight was started by a commit.</summary>
        public bool StartedByCommit;

        /// <summary>The fault that ended the evidence subscription, or <c>null</c>.</summary>
        public Exception? EvidenceFault;
    }
}
